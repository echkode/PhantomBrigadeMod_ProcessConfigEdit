// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using HarmonyLib;

using PhantomBrigade.Data;

using QFSW.QC;
using QFSW.QC.Parsers;

using UnityEngine;

namespace EchKode.PBMods.ProcessConfigEdit
{
	static partial class ModUtilities
	{
		internal enum EditOperation
		{
			Overwrite = 0,
			Insert,
			Remove,
			DefaultValue,
			NullValue,
			SetContext,
		}

		internal sealed partial class EditSpec
		{
			public string dataTypeName;
			public object root;
			public string filename;
			public string fieldPath;
			public string valueRaw;
			public int modIndex;
			public string modID;
			public List<PathContext> pathContexts;
			public EditState state = new EditState();
		}

		internal class PathContext
		{
			public string fieldSegments;
			public object parent;
			public object target;
			public Type targetType;
			public int targetIndex;
			public object targetKey;
			public FieldInfo fieldInfo;
		}

		internal sealed class EditState : PathContext
		{
			public bool faulted;
			public EditOperation op;
			public int pathSegmentCount;
			public int pathSegmentIndex;
			public string pathSegment;
			public bool atEndOfPath;
		}

		private static class Constants
		{
			public static class Operator
			{
				public const string Insert = "!+";
				public const string Remove = "!-";
				public const string DefaultValue = "!d";
				public const string NullValue = "!n";
				public const string SetContext = "!^";
			}

			public const char PathSeparator = '.';
			public const char ContextTokenChar = '^';
			public const string ContextToken = "^";
			public const string IndexGlobToken = "*";
		}

		private static Type typeString;
		private static Type typeBool;
		private static Type typeInt;
		private static Type typeFloat;
		private static Type typeVector2;
		private static Type typeVector3;
		private static Type typeVector4;
		private static Type typeColor;
		private static Type typeIList;
		private static Type typeHashSet;
		private static Type typeIDictionary;
		private static Type typeEnum;

		private static HashSet<Type> allowedKeyTypes;
		private static HashSet<Type> terminalTypes;
		private static Dictionary<string, EditOperation> operationMap;
		private static HashSet<EditOperation> allowedHashSetOperations;
		private static Dictionary<Type, Action<EditSpec, Action<object>>> updaterMap;
		private static Dictionary<Type, object> defaultValueMap;
		private static ColorParser colorParser;

		internal static void Initialize()
		{
			typeString = typeof(string);
			typeBool = typeof(bool);
			typeInt = typeof(int);
			typeFloat = typeof(float);
			typeVector2 = typeof(Vector2);
			typeVector3 = typeof(Vector3);
			typeVector4 = typeof(Vector4);
			typeColor = typeof(Color);
			typeIList = typeof(IList);
			typeHashSet = typeof(HashSet<string>);
			typeIDictionary = typeof(IDictionary);
			typeEnum = typeof(Enum);

			allowedKeyTypes = new HashSet<Type>()
			{
				typeString,
				typeInt,
			};

			terminalTypes = new HashSet<Type>()
			{
				typeString,
				typeBool,
				typeInt,
				typeFloat,
				typeEnum,
			};

			operationMap = new Dictionary<string, EditOperation>()
			{
				[Constants.Operator.Insert] = EditOperation.Insert,
				[Constants.Operator.Remove] = EditOperation.Remove,
				[Constants.Operator.DefaultValue] = EditOperation.DefaultValue,
				[Constants.Operator.NullValue] = EditOperation.NullValue,
				[Constants.Operator.SetContext] = EditOperation.SetContext,
			};

			allowedHashSetOperations = new HashSet<EditOperation>()
			{
				EditOperation.Insert,
				EditOperation.Remove,
				EditOperation.DefaultValue,
			};

			updaterMap = new Dictionary<Type, Action<EditSpec, Action<object>>>()
			{
				[typeString] = UpdateStringField,
				[typeBool] = UpdateBoolField,
				[typeInt] = UpdateIntField,
				[typeFloat] = UpdateFloatField,
				[typeVector2] = UpdateVector2Field,
				[typeVector3] = UpdateVector3Field,
				[typeVector4] = UpdateVector4Field,
				[typeColor] = UpdateColorField,
				[typeHashSet] = UpdateHashSet,
				[typeEnum] = UpdateEnum,
			};

			if (ModLink.Settings.logging)
			{
				var tagTypeMap = UtilitiesYAML.GetTagMappings();
				Debug.LogFormat(
					"Mod {0} ({1}) YAML tags ({2}):\n  {3}",
					ModLink.modIndex,
					ModLink.modID,
					tagTypeMap.Count,
					tagTypeMap.ToStringFormattedKeyValuePairs(true, multilinePrefix: "  "));
			}

			defaultValueMap = new Dictionary<Type, object>()
			{
				[typeString] = "",
				[typeInt] = 0,
				[typeFloat] = 0f,
				[typeVector3] = Vector3.zero,
			};

			colorParser = new ColorParser();
		}

		internal static string FindConfigKeyIfEmpty(
			object target,
			string dataTypeName,
			string key)
		{
			if (!string.IsNullOrEmpty(key))
			{
				return key;
			}

			var multilinker = typeof(DataContainerSubsystem).Assembly.GetTypes()
				.Where(t => t.Name.StartsWith("DataMultiLinker"))
				.Select(t => t.BaseType)
				.Where(t => t.IsGenericType)
				.Where(t => t.GenericTypeArguments.Any(gt => gt.Name == dataTypeName))
				.SingleOrDefault();
			if (multilinker != null)
			{
				var fi = AccessTools.DeclaredField(multilinker, "dataInternal");
				var d = (IDictionary)fi.GetValue(null);
				foreach (var k in d.Keys)
				{
					if (ReferenceEquals(d[k], target))
					{
						return (string)k;
					}
				}
			}

			return key;
		}

		internal static void ProcessFieldEdit(EditSpec spec)
		{
			if (string.IsNullOrEmpty(spec.fieldPath) || string.IsNullOrEmpty(spec.valueRaw))
			{
				ReportFault(
					spec,
					"fails to edit",
					"Missing field path ({0}) or raw value ({1})",
					spec.fieldPath,
					spec.valueRaw);
				return;
			}

			var (eop, valueRaw) = ParseOperation(spec.valueRaw);
			spec.state.op = eop;
			spec.valueRaw = valueRaw;

			spec.state.parent = spec.root;
			spec.state.target = spec.root;
			spec.state.targetType = null;
			spec.state.targetIndex = -1;
			spec.state.targetKey = null;
			spec.state.fieldInfo = null;

			if (!WalkFieldPath(spec))
			{
				return;
			}

			if (spec.state.op == EditOperation.SetContext)
			{
				if (terminalTypes.Contains(spec.state.targetType))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Cannot set context -- type {0} is terminal",
						spec.state.targetType);
					return;
				}

				PushPathContext(spec);
				return;
			}

			var (ok, update) = ValidateEditState(spec);
			if (!ok)
			{
				return;
			}

			if (spec.state.op == EditOperation.NullValue)
			{
				if (spec.state.targetType.IsValueType)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Value type {0} cannot be set to null",
						spec.state.targetType);
					return;
				}

				spec.state.fieldInfo.SetValue(spec.state.parent, null);
				Report(
					spec,
					"edits",
					"Assigning null to target field");
				if (IsFieldPathInContext(spec))
				{
					PopPathContext(spec);
				}
				return;
			}

			if (updaterMap.TryGetValue(spec.state.targetType, out var updater))
			{
				updater(spec, update);
				return;
			}

			if (spec.state.op == EditOperation.Overwrite)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Value type {0} has no string parsing implementation -- try using {1} keyword if you're after filling it with default instance",
					spec.state.targetType,
					Constants.Operator.DefaultValue);
				return;
			}

			if (spec.state.op != EditOperation.DefaultValue)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Can't apply {0} operation at this point in the field path",
					spec.state.op);
				return;
			}

			var instanceType = spec.state.targetType;
			var isTag = valueRaw.StartsWith("!");
			if (isTag)
			{
				if (!UtilitiesYAML.GetTagMappings().TryGetValue(valueRaw, out instanceType))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"There is no type associated with tag {0}",
						valueRaw);
					return;
				}
				if (!spec.state.targetType.IsAssignableFrom(instanceType))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Tag type {0} is not compatible with field type {1} | tag: {2}",
						instanceType,
						spec.state.targetType,
						valueRaw);
					return;
				}
			}

			if (spec.state.targetIndex != -1)
			{
				var list = (IList)spec.state.parent;
				list[spec.state.targetIndex] = Activator.CreateInstance(instanceType);
				if (!terminalTypes.Contains(spec.state.targetType))
				{
					PushPathContext(spec, list[spec.state.targetIndex]);
				}
				Report(
					spec,
					"edits",
					"Assigning new default object of type {0} to target index {1}",
					instanceType,
					spec.state.targetIndex);
				return;
			}

			if (spec.state.targetKey != null)
			{
				var map = (IDictionary)spec.state.parent;
				map[spec.state.targetKey] = Activator.CreateInstance(instanceType);
				if (!terminalTypes.Contains(spec.state.targetType))
				{
					PushPathContext(spec, map[spec.state.targetKey]);
				}
				Report(
					spec,
					"edits",
					"Assigning new default object of type {0} to target key {1}",
					instanceType,
					spec.state.targetKey);
				return;
			}

			if (spec.state.fieldInfo == null)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"no target field info -- WalkFieldPath() failed to terminate properly | {0}",
					spec);
				return;
			}

			var instance = Activator.CreateInstance(instanceType);
			spec.state.fieldInfo.SetValue(spec.state.parent, instance);
			if (!terminalTypes.Contains(spec.state.targetType))
			{
				PushPathContext(spec, spec.state.fieldInfo.GetValue(spec.state.parent));
			}
			Report(
				spec,
				"edits",
				"Assigning new default object of type {0} to target field",
				instanceType);
		}

		private static (EditOperation, string) ParseOperation(string valueRaw)
		{
			foreach (var kvp in operationMap)
			{
				var opr = kvp.Key;
				if (valueRaw.EndsWith(opr))
				{
					return (kvp.Value, valueRaw.Replace(opr, "").TrimEnd(' '));
				}
			}
			return (EditOperation.Overwrite, valueRaw);
		}

		private static bool WalkFieldPath(EditSpec spec)
		{
			var pathSegments = spec.fieldPath.Split(Constants.PathSeparator);
			spec.state.pathSegmentCount = pathSegments.Length;

			for (var i = 0; i < pathSegments.Length; i += 1)
			{
				spec.state.pathSegmentIndex = i;
				spec.state.pathSegment = pathSegments[i];
				spec.state.atEndOfPath = spec.state.pathSegmentIndex == spec.state.pathSegmentCount - 1;

				if (spec.state.target == null)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Can't proceed past {0} (I{1} S{2}/{3}) -- current target reference is null",
						spec.state.pathSegment,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						fieldSegmentCountFormatter);
					return false;
				}

				var root = i == 0;
				if (spec.state.pathSegment.StartsWith(Constants.ContextToken))
				{
					if (!root)
					{
						ReportFault(
							spec,
							"attempts to edit",
							"Can't proceed past {0} (I{1} S{2}/{3}) -- context token only recognized in root segment",
							spec.state.pathSegment,
							spec.state.pathSegmentIndex,
							spec.state.pathSegmentIndex + 1,
							fieldSegmentCountFormatter);
						return false;
					}

					if (!spec.state.pathSegment.All(c => c == Constants.ContextTokenChar))
					{
						ReportFault(
							spec,
							"attempts to edit",
							"Can't proceed past {0} (I{1} S{2}/{3}) -- unrecognized special token",
							spec.state.pathSegment,
							spec.state.pathSegmentIndex,
							spec.state.pathSegmentIndex + 1,
							fieldSegmentCountFormatter);
						return false;
					}

					var contextLevel = spec.state.pathSegment.Length;
					if (!UsePathContext(spec, contextLevel))
					{
						return false;
					}

					spec.state.pathSegmentIndex += CountContextFieldPathSegments(spec);
					continue;
				}

				if (root && spec.pathContexts.Count != 0)
				{
					Report(
						spec,
						"clears context in",
						"No context in root | segment: {0}",
						spec.state.pathSegment);
					spec.pathContexts.Clear();
				}

				spec.state.pathSegmentIndex += CountContextFieldPathSegments(spec);
				spec.state.targetType = spec.state.target.GetType();

				var child = spec.state.pathSegmentIndex != 0;
				if (child && typeIList.IsAssignableFrom(spec.state.targetType))
				{
					if (!ProduceListElement(spec))
					{
						return false;
					}
				}
				else if (child && typeIDictionary.IsAssignableFrom(spec.state.targetType))
				{
					if (!ProduceMapEntry(spec))
					{
						return false;
					}
				}
				else if (!ProduceField(spec))
				{
					return false;
				}
			}

			return true;
		}

		private static bool ProduceListElement(EditSpec spec)
		{
			var list = spec.state.target as IList;
			var result = -1;
			if (spec.state.atEndOfPath && spec.state.pathSegment == Constants.IndexGlobToken)
			{
				result = list.Count;
			}
			else if (!int.TryParse(spec.state.pathSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < 0)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Index {0} (I{1} S{2}/{3}) can't be parsed or is negative",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter);
				return false;
			}

			var listType = list.GetType();
			var elementType = listType.IsArray
				? listType.GetElementType()
				: listType.GetGenericArguments()[0];
			if (spec.state.atEndOfPath && spec.state.op != EditOperation.SetContext)
			{
				if (!EditList(spec, list, result, elementType))
				{
					return false;
				}
			}
			else if (result >= list.Count)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Can't proceed past {0} (I{1} S{2}/{3}) -- current target reference is beyond end of list (size={4})",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter,
					list.Count);
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.target = list[result];
			spec.state.targetType = elementType;
			spec.state.targetIndex = result;
			spec.state.targetKey = null;
			spec.state.fieldInfo = null;

			return true;
		}

		private static bool EditList(
			EditSpec spec,
			IList list,
			int index,
			Type elementType)
		{
			var outOfBounds = index >= list.Count;

			if (spec.state.op == EditOperation.Insert)
			{
				var emptyValue = string.IsNullOrWhiteSpace(spec.valueRaw);
				var instance = DefaultValue(elementType);
				if (instance == null && emptyValue)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Default value for list insert is null (I{0} S{1}/{2}) -- likely missing a YAML tag",
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						fieldSegmentCountFormatter);
					return false;
				}

				if (outOfBounds)
				{
					list.Add(instance);
					Report(
						spec,
						"edits",
						"Adding new entry of type {0} to end of the list",
						elementType);
					if (spec.state.pathSegment == Constants.IndexGlobToken)
					{
						Report(
							spec,
							"resolves index in",
							"List index is now {0}",
							index);
						spec.state.pathSegment = index.ToString();
						spec.fieldPath = spec.fieldPath.Replace(Constants.IndexGlobToken, spec.state.pathSegment);
					}
				}
				else
				{
					list.Insert(index, instance);
					Report(
						spec,
						"edits",
						"Inserting new entry of type {0} to index {1} of the list",
						elementType,
						index);
				}

				if (emptyValue && !terminalTypes.Contains(elementType))
				{
					// An empty value will end this edit step so we have to push context here.
					var newIndex = outOfBounds ? list.Count - 1 : index;
					PushPathContext(spec, new PathContext()
					{
						parent = spec.state.target,
						target = list[newIndex],
						targetType = elementType,
						targetIndex = newIndex,
					});
				}

				var isTag = !emptyValue && elementType != typeString && spec.valueRaw.StartsWith("!");
				if (isTag)
				{
					spec.state.op = EditOperation.DefaultValue;
				}

				return !emptyValue;
			}

			if (spec.state.op == EditOperation.Remove)
			{
				if (outOfBounds)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Index {0} (I{1} S{2}/{3}) can't be removed as it's out of bounds for list size {4}",
						spec.state.pathSegment,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						fieldSegmentCountFormatter,
						list.Count);
					return false;
				}

				list.RemoveAt(index);
				Report(
					spec,
					"edits",
					"Removing entry at index {0} of the list",
					index);

				var contextSegments = "";
				if (IsFieldPathInContext(spec))
				{
					contextSegments = spec.pathContexts.Last().fieldSegments;
					PopPathContext(spec);
				}

				TrimFieldPath(spec, contextSegments);
				if (!IsFieldPathInContext(spec))
				{
					PushPathContext(spec);
				}

				return false;
			}

			if (outOfBounds)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Index {0} (I{1} S{2}/{3}) can't be replaced as it's out of bounds for list size {4}",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter,
					list.Count);
				return false;
			}

			return true;
		}

		private static bool ProduceMapEntry(EditSpec spec)
		{
			var map = spec.state.target as IDictionary;
			var entryTypes = map.GetType().GetGenericArguments();
			var keyType = entryTypes[0];
			var valueType = entryTypes[1];

			if (!allowedKeyTypes.Contains(keyType))
			{
				var permittedTypes = string.Join(", ", allowedKeyTypes.Select(t => t.GetNiceTypeName()));
				ReportFault(
					spec,
					"attempts to edit",
					"Unable to produce map entry (I{0} S{1}/{2}) - only keys of types [{3}] are supported",
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter,
					permittedTypes);
				return false;
			}

			var key = spec.state.pathSegment;
			var (keyOK, resolvedKey) = ResolveTargetKey(map.GetType(), key);
			if (!keyOK)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Unable to produce map entry for key {0} (I{1} S{2}/{3}) -- key can't be coerced to the correct type",
					key,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter);
				return false;
			}
			var entryExists = map.Contains(resolvedKey);

			if (spec.state.atEndOfPath && spec.state.op != EditOperation.SetContext)
			{
				if (!EditMap(
					spec,
					map,
					valueType,
					resolvedKey,
					entryExists))
				{
					return false;
				}
			}
			else if (!entryExists)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Can't proceed past {0} (I{1} S{2}/{3}), current target reference doesn't exist in dictionary)",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter);
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.target = map[key];
			spec.state.targetType = valueType;
			spec.state.targetIndex = -1;
			spec.state.targetKey = resolvedKey;
			spec.state.fieldInfo = null;

			return true;
		}

		private static bool EditMap(
			EditSpec spec,
			IDictionary map,
			Type valueType,
			object key,
			bool entryExists)
		{
			if (spec.state.op == EditOperation.Insert)
			{
				var emptyValue = string.IsNullOrWhiteSpace(spec.valueRaw);
				if (!entryExists)
				{
					var instance = DefaultValue(valueType);
					if (instance == null && emptyValue)
					{
						ReportFault(
							spec,
							"attempts to edit",
							"Default value for insert with key {0} is null (I{1} S{2}/{3}) -- likely missing a YAML tag",
							key,
							spec.state.pathSegmentIndex,
							spec.state.pathSegmentIndex + 1,
							fieldSegmentCountFormatter);
						return false;
					}
					map.Add(key, instance);
					Report(
						spec,
						"edits",
						"Adding key {0} to target dictionary",
						key);
				}
				else
				{
					Report(
						spec,
						"attempts to edit",
						"Key {0} already exists, ignoring the command to add it",
						key);
				}

				if (emptyValue && !terminalTypes.Contains(valueType))
				{
					// An empty value will end this edit step so we have to push context here.
					PushPathContext(spec, new PathContext()
					{
						parent = spec.state.parent,
						target = map[key],
						targetType = valueType,
						targetIndex = -1,
						targetKey = key,
					});
				}

				var isTag = !emptyValue && valueType != typeString && spec.valueRaw.StartsWith("!");
				if (isTag)
				{
					spec.state.op = EditOperation.DefaultValue;
				}

				return !emptyValue;
			}

			if (spec.state.op == EditOperation.Remove)
			{
				if (!entryExists)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Key {0} (I{1} S{2}/{3}) can't be removed from target dictionary -- it can't be found",
						key,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						fieldSegmentCountFormatter);
					return false;
				}

				Report(
					spec,
					"edits",
					"Removing key {0} from target dictionary",
					key);
				map.Remove(key);

				var contextSegments = "";
				if (IsFieldPathInContext(spec))
				{
					contextSegments = spec.pathContexts.Last().fieldSegments;
					PopPathContext(spec);
				}

				TrimFieldPath(spec, contextSegments);
				if (!IsFieldPathInContext(spec))
				{
					PushPathContext(spec);
				}

				return false;
			}

			return true;
		}

		private static bool ProduceField(EditSpec spec)
		{
			var field = spec.state.targetType.GetField(spec.state.pathSegment);
			if (field == null)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Field {0} (I{1} S{2}/{3}) could not be found on type {4}",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter,
					spec.state.targetType);
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.target = field.GetValue(spec.state.target);
			spec.state.targetType = field.FieldType;
			spec.state.targetIndex = -1;
			spec.state.targetKey = null;
			spec.state.fieldInfo = field;

			return true;
		}

		private static bool UsePathContext(EditSpec spec, int contextLevel)
		{
			if (contextLevel > spec.pathContexts.Count)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Can't proceed past {0} (I{1} S{2}/{3}) -- refers to more context levels ({4}) than are on stack ({5})",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount,
					contextLevel,
					spec.pathContexts.Count);
				return false;
			}

			while (contextLevel < spec.pathContexts.Count)
			{
				ReportContext(
					spec,
					"levels down context in",
					"Removed context: {0} | context level: {1}",
					contextFieldPathFormatter,
					spec.pathContexts.Count);
				spec.pathContexts.RemoveAt(spec.pathContexts.Count - 1);
			}

			var pathContext = spec.pathContexts[contextLevel - 1];
			spec.state.parent = pathContext.parent;
			spec.state.target = pathContext.target;
			spec.state.targetType = pathContext.targetType;
			spec.state.targetIndex = pathContext.targetIndex;
			spec.state.targetKey = pathContext.targetKey;
			spec.state.fieldInfo = pathContext.fieldInfo;

			ReportContext(
				spec,
				"using context in",
				"Context: {0} | context level: {1} | target type: {2} | target {3}",
				contextFieldPathFormatter,
				spec.pathContexts.Count,
				spec.state.targetType?.GetNiceTypeName() ?? "<unknown>",
				spec.state.target == null ? "is null" : "has value");

			return true;
		}

		private static void PushPathContext(EditSpec spec) => PushPathContext(spec, spec.state.target);
		private static void PushPathContext(EditSpec spec, object target) =>
			PushPathContext(spec, new PathContext()
			{
				parent = spec.state.parent,
				target = target,
				targetType = spec.state.targetType,
				targetIndex = spec.state.targetIndex,
				targetKey = spec.state.targetKey,
				fieldInfo = spec.state.fieldInfo,
			});
		private static void PushPathContext(EditSpec spec, PathContext pathContext)
		{
			pathContext.fieldSegments = spec.fieldPath;
			if (spec.fieldPath.StartsWith(Constants.ContextToken))
			{
				var pos = spec.fieldPath.IndexOf(Constants.PathSeparator);
				if (pos == -1)
				{
					pathContext.fieldSegments = spec.pathContexts.Last().fieldSegments;
					spec.pathContexts[spec.pathContexts.Count - 1] = pathContext;
					ReportContext(
						spec,
						"changes context values in",
						"Context: {0} | context level: {1}",
						contextFieldPathFormatter,
						spec.pathContexts.Count);
					return;
				}

				pathContext.fieldSegments = spec.fieldPath.Substring(pos);
			}
			spec.pathContexts.Add(pathContext);
			ReportContext(
				spec,
				"assigns context in",
				"Added context: {0} | context level: {1}",
				contextFieldPathFormatter,
				spec.pathContexts.Count);
		}

		private static void TryPushPathContext(EditSpec spec)
		{
			TrimFieldPath(spec);
			if (IsFieldPathInContext(spec))
			{
				return;
			}
			PushPathContext(spec);
		}

		private static void PopPathContext(EditSpec spec)
		{
			if (spec.pathContexts.Count == 0)
			{
				return;
			}
			ReportContext(
				spec,
				"removes context in",
				"Removed context: {0} | context level: {1}",
				contextFieldPathFormatter,
				spec.pathContexts.Count);
			spec.pathContexts.RemoveAt(spec.pathContexts.Count - 1);
		}

		private static (bool, Action<object>) ValidateEditState(EditSpec spec)
		{
			if (spec.state.parent == null)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Arrived at a null parent after walking field path (I{0} S{1}/{2})",
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					fieldSegmentCountFormatter);
				return (false, null);
			}

			var parentType = spec.state.parent.GetType();
			var parentIsList = typeIList.IsAssignableFrom(parentType);
			if (parentIsList)
			{
				if (spec.state.targetIndex == -1)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Value is contained in a list but list index {0} is not valid",
						spec.state.pathSegment);
					return (false, null);
				}

				var parentList = (IList)spec.state.parent;
				var targetIndex = spec.state.targetIndex;
				return (true, v => parentList[targetIndex] = v);
			}

			var parentIsMap = typeIDictionary.IsAssignableFrom(parentType);
			if (parentIsMap)
			{
				if (spec.state.targetKey == null)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Value is contained in a dictionary but the key {0} is not valid",
						spec.state.pathSegment);
					return (false, null);
				}

				var parentMap = (IDictionary)spec.state.parent;
				var targetKey = spec.state.targetKey;
				return (true, v => parentMap[targetKey] = v);
			}

			if (spec.state.fieldInfo == null)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Value can't be modified due to missing field info");
				return (false, null);
			}

			var fieldIsEnum = typeEnum.IsAssignableFrom(spec.state.targetType);
			if (fieldIsEnum)
			{
				spec.state.targetType = typeEnum;
			}

			var parent = spec.state.parent;
			var fieldInfo = spec.state.fieldInfo;
			return (true, v => fieldInfo.SetValue(parent, v));
		}

		private static (bool, object) ResolveTargetKey(Type parentType, string targetKey)
		{
			if (!parentType.IsGenericType)
			{
				return (false, targetKey);
			}
			if (!parentType.IsConstructedGenericType)
			{
				return (false, targetKey);
			}

			var typeArgs = parentType.GetGenericArguments();
			if (typeArgs.Length != 2)
			{
				return (false, targetKey);
			}

			var keyType = typeArgs[0];
			if (keyType == typeof(string))
			{
				return (true, targetKey);
			}

			if (keyType == typeof(int))
			{
				if (!int.TryParse(targetKey, out var intKey))
				{
					return (false, targetKey);
				}
				return (true, intKey);
			}

			return (false, targetKey);
		}

		private static void UpdateStringField(EditSpec spec, Action<object> update)
		{
			var v = spec.state.op != EditOperation.DefaultValue ? spec.valueRaw : null;
			update(v);
			Report(
				spec,
				"edits",
				"String field modified with value {0}",
				v);
		}

		private static void UpdateBoolField(EditSpec spec, Action<object> update)
		{
			var v = spec.state.op != EditOperation.DefaultValue
				&& string.Equals(spec.valueRaw, "true", StringComparison.OrdinalIgnoreCase);
			update(v);
			Report(
				spec,
				"edits",
				"Bool field modified with value {0}",
				v);
		}

		private static void UpdateIntField(EditSpec spec, Action<object> update)
		{
			var v = 0;
			if (spec.state.op != EditOperation.DefaultValue)
			{
				if (!int.TryParse(spec.valueRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Integer field can't be overwritten -- can't parse raw value {0}",
						spec.valueRaw);
					return;
				}
			}

			update(v);
			Report(
				spec,
				"edits",
				"Integer field modified with value {0}",
				v);
		}

		private static void UpdateFloatField(EditSpec spec, Action<object> update)
		{
			var v = 0.0f;
			if (spec.state.op != EditOperation.DefaultValue)
			{
				if (!float.TryParse(spec.valueRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Float field can't be overwritten -- can't parse raw value {0}",
						spec.valueRaw);
					return;
				}
			}

			update(v);
			Report(
				spec,
				"edits",
				"Float field modified with value {0}",
				v);
		}

		private static void UpdateVector2Field(EditSpec spec, Action<object> update)
		{
			UpdateVectorField(
				spec,
				update,
				2,
				ary => new Vector2(ary[0], ary[1]),
				Vector2.zero);
		}

		private static void UpdateVector3Field(EditSpec spec, Action<object> update)
		{
			UpdateVectorField(
				spec,
				update,
				3,
				ary => new Vector3(ary[0], ary[1], ary[2]),
				Vector3.zero);
		}

		private static void UpdateVector4Field(EditSpec spec, Action<object> update)
		{
			UpdateVectorField(
				spec,
				update,
				4,
				ary => new Vector4(ary[0], ary[1], ary[2], ary[3]),
				Vector4.zero);
		}

		private static void UpdateColorField(EditSpec spec, Action<object> update)
		{
			var v = Color.black;
			if (spec.state.op != EditOperation.DefaultValue)
			{
				if (!spec.valueRaw.StartsWith("(") || !spec.valueRaw.EndsWith(")"))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Color field can't be overwritten - can't parse raw value {0} - missing parentheses",
						spec.valueRaw);
					return;
				}
				try
				{
					v = colorParser.Parse(spec.valueRaw.Substring(1, spec.valueRaw.Length - 2));
				}
				catch (ParserInputException ex)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Can't parse raw value {0} -- {1}",
						spec.valueRaw,
						ex.Message);
					return;
				}
			}

			update(v);
			PushPathContext(spec, v);
			Report(
				spec,
				"edits",
				"Color field modified with value {0}",
				v);
		}

		private static void UpdateVectorField(
			EditSpec spec,
			Action<object> update,
			int vectorLength,
			Func<float[], object> ctor,
			object zero)
		{
			var v = zero;
			if (spec.state.op != EditOperation.DefaultValue)
			{
				var (ok, parsed) = ParseVectorValue(spec, vectorLength, ctor);
				if (!ok)
				{
					return;
				}
				v = parsed;
			}

			update(v);
			PushPathContext(spec, v);
			Report(
				spec,
				"edits",
				"Vector{0} field modified with value {1}",
				vectorLength,
				v);
		}

		private static (bool, object) ParseVectorValue(
			EditSpec spec,
			int vectorLength,
			Func<float[], object> ctor)
		{
			if (!spec.valueRaw.StartsWith("(") || !spec.valueRaw.EndsWith(")"))
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Vector{0} field can't be overwritten -- can't parse raw value {1} - missing parentheses",
					vectorLength,
					spec.valueRaw);
				return (false, null);
			}

			var valueRaw = spec.valueRaw.Substring(1, spec.valueRaw.Length - 2);
			var velems = valueRaw.Split(',');
			if (velems.Length != vectorLength)
			{
				ReportFault(
					spec,
					"attempts to edit",
					"Vector{0} field can't be overwritten -- can't parse raw value {1} - invalid number of elements",
					vectorLength,
					spec.valueRaw);
				return (false, null);
			}

			var parsed = new float[velems.Length];
			for (var i = 0; i < velems.Length; i += 1)
			{
				if (!float.TryParse(velems[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Vector{0} field can't be overwritten -- can't parse raw value {1}",
						vectorLength,
						spec.valueRaw);
					return (false, null);
				}
				parsed[i] = result;
			}

			return (true, ctor(parsed));
		}

		private static void UpdateHashSet(EditSpec spec, Action<object> _)
		{
			if (!allowedHashSetOperations.Contains(spec.state.op))
			{
				ReportFault(
					spec,
					"attempts to edit",
					"No addition or removal keywords detected -- no other operations are supported on hashsets");
				return;
			}

			if (spec.state.op == EditOperation.DefaultValue)
			{
				if (spec.state.target != null)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Hashset exists -- cannot replace with default value");
					return;
				}

				spec.state.fieldInfo.SetValue(spec.state.parent, new HashSet<string>());
				PushPathContext(spec, spec.state.fieldInfo.GetValue(spec.state.parent));
				Report(
					spec,
					"edits",
					"Assigning new hashset to target field");
				return;
			}

			var stringSet = spec.state.target as HashSet<string>;
			var found = stringSet.Contains(spec.valueRaw);

			switch (spec.state.op)
			{
				case EditOperation.Insert:
					if (found)
					{
						Report(
							spec,
							"attempts to edit",
							"Value {0} already exists in target set, ignoring addition command prompted by {1} keyword",
							spec.valueRaw,
							Constants.Operator.Insert);
						return;
					}
					stringSet.Add(spec.valueRaw);
					Report(
						spec,
						"edits",
						"Value {0} is added to target set due to {1} keyword",
						spec.valueRaw,
						Constants.Operator.Insert);
					TryPushPathContext(spec);
					break;
				case EditOperation.Remove:
					if (!found)
					{
						Report(
							spec,
							"attempts to edit",
							"Value {0} doesn't exist in target set, ignoring removal command prompted by {1} keyword",
							spec.valueRaw,
							Constants.Operator.Remove);
						return;
					}
					stringSet.Remove(spec.valueRaw);
					Report(
						spec,
						"edits",
						"Value {0} is removed from target set due to {1} keyword",
						spec.valueRaw,
						Constants.Operator.Remove);
					TryPushPathContext(spec);
					break;
			}
		}

		private static void UpdateEnum(EditSpec spec, Action<object> update)
		{
			var targetType = spec.state.fieldInfo.FieldType;
			var values = Enum.GetValues(targetType);
			// This makes the assumption that the bottom value of the enum also has the lowest
			// unsigned integer value.
			var v = values.GetValue(0);

			if (spec.state.op != EditOperation.DefaultValue)
			{
				var names = Enum.GetNames(targetType);
				var idx = Array.FindIndex(names, name => string.CompareOrdinal(name, spec.valueRaw) == 0);
				if (idx == -1)
				{
					ReportFault(
						spec,
						"attempts to edit",
						"Enum field can't be overwritten -- can't parse raw value | type: {0} | value: {1}",
						targetType,
						spec.valueRaw);
					return;
				}
				v = values.GetValue(idx);
			}

			update(v);
			Report(
				spec,
				"edits",
				"Enum field modified with value {0}",
				v);
		}

		private static object DefaultValue(Type elementType)
		{
			if (defaultValueMap.TryGetValue(elementType, out var value))
			{
				return value;
			}
			if (elementType.IsInterface)
			{
				return null;
			}
			return Activator.CreateInstance(elementType);
		}

		private static void Report(EditSpec spec, string verb, string fmt, params object[] args)
		{
			if (!ModLink.Settings.logging)
			{
				return;
			}
			(fmt, args) = PrepareReportArgs(spec, verb, fmt, args);
			Debug.LogFormat(fmt, args);
		}

		private static void ReportContext(EditSpec spec, string verb, string fmt, params object[] args)
		{
			if (!ModLink.Settings.logContext)
			{
				return;
			}
			(fmt, args) = PrepareReportArgs(spec, verb, fmt, args);
			Debug.LogFormat(fmt, args);
		}

		private static void ReportFault(EditSpec spec, string verb, string fmt, params object[] args)
		{
			spec.state.faulted = true;
			(fmt, args) = PrepareReportArgs(spec, verb, fmt, args);
			Debug.LogWarningFormat(fmt, args);
		}

		private static (string Format, object[] Args) PrepareReportArgs(EditSpec spec, string verb, string fmt, params object[] args)
		{
			var fixedFields = new object[]
			{
				spec.modIndex,
				spec.modID,
				verb,
				spec.filename,
				spec.dataTypeName,
				ReplacePathContextInFieldPath(spec)
			};
			fmt = reFormatFieldSpecifier.Replace(fmt, RenumberSpecifier);
			for (var i = 0; i < args.Length; i += 1)
			{
				if (args[i] is Type tt)
				{
					args[i] = tt.GetNiceTypeName();
				}
				else if (typeof(SpecFormatter).IsAssignableFrom(args[i].GetType()))
				{
					var f = (SpecFormatter)args[i];
					args[i] = f(spec);
				}
			}

			return ("Mod {0} ({1}) {2} config {3} of type {4} | field: {5} | " + fmt, fixedFields.Concat(args).ToArray());

			string RenumberSpecifier(Match m)
			{
				var i = int.Parse(m.Value.Substring(1, m.Value.Length - 2)) + 6;
				return "{" + i + "}";
			}
		}

		private static readonly Regex reFormatFieldSpecifier = new Regex(@"\{\d+\}");

		internal static string ReplacePathContextInFieldPath(EditSpec spec)
		{
			if (spec.fieldPath.Length == 0)
			{
				return spec.fieldPath;
			}
			if (!spec.fieldPath.StartsWith(Constants.ContextToken))
			{
				return spec.fieldPath;
			}
			if (spec.pathContexts.Count == 0)
			{
				return spec.fieldPath;
			}

			var pos = spec.fieldPath.IndexOf(Constants.PathSeparator);
			var contextSegment = pos == -1 ? spec.fieldPath : spec.fieldPath.Substring(0, pos);
			if (!contextSegment.All(c => c == Constants.ContextTokenChar))
			{
				return spec.fieldPath;
			}

			var contextCount = contextSegment.Length;
			var remainder = pos == -1 ? "" : spec.fieldPath.Substring(pos);
			var k = Math.Min(contextCount, spec.pathContexts.Count);
			var segments = spec.pathContexts.Take(k).Select(pc => pc.fieldSegments);
			if (contextCount > spec.pathContexts.Count)
			{
				segments = segments.Concat(Enumerable.Repeat(".?", contextCount - spec.pathContexts.Count));
			}
			return string.Join("", segments) + remainder;
		}

		private static string BuildContextFieldPath(EditSpec spec) =>
			string.Join("", spec.pathContexts.Select(pc => pc.fieldSegments));

		private delegate string SpecFormatter(EditSpec spec);
		private static readonly SpecFormatter contextFieldPathFormatter = s => BuildContextFieldPath(s);
		private static readonly SpecFormatter fieldSegmentCountFormatter = s => CountFieldPathSegments(s).ToString();

		private static int CountContextFieldPathSegments(EditSpec spec)
		{
			var segmentCount = 0;
			foreach (var pc in spec.pathContexts)
			{
				for (var pos = pc.fieldSegments.IndexOf(Constants.PathSeparator);
					pos != -1;
					pos = pc.fieldSegments.IndexOf(Constants.PathSeparator, pos + 1))
				{
					segmentCount += 1;
				}
			}
			return segmentCount;
		}

		private static int CountFieldPathSegments(EditSpec spec) =>
			spec.state.pathSegmentCount
			+ (spec.fieldPath.StartsWith(Constants.ContextToken)
				? CountContextFieldPathSegments(spec) - 1
				: 0);

		private static void TrimFieldPath(EditSpec spec, string contextSegments = "")
		{
			var pos = spec.fieldPath.LastIndexOf(Constants.PathSeparator);
			if (pos != -1)
			{
				spec.fieldPath = spec.fieldPath.Substring(0, pos);
				spec.state.pathSegmentIndex -= 1;
				spec.state.pathSegmentCount -= 1;
				return;
			}

			if (string.IsNullOrEmpty(contextSegments))
			{
				return;
			}
			if (spec.fieldPath.Length == 1)
			{
				// This is a programming error
				return;
			}

			spec.fieldPath = spec.fieldPath.Substring(0, spec.fieldPath.Length - 1);
			for (pos = contextSegments.IndexOf(Constants.PathSeparator);
				pos != -1;
				pos = contextSegments.IndexOf(Constants.PathSeparator, pos + 1))
			{
				spec.state.pathSegmentIndex -= 1;
				spec.state.pathSegmentCount -= 1;
			}
		}

		private static bool IsFieldPathInContext(EditSpec spec)
		{
			if (spec.pathContexts.Count == 0)
			{
				return false;
			}

			var pos = spec.fieldPath.IndexOf(Constants.PathSeparator);
			if (pos != -1)
			{
				return false;
			}
			return spec.fieldPath.StartsWith(Constants.ContextToken);
		}

		partial class EditSpec
		{
			public const string pipeDelimiter = " | ";
			public string ToDelimitedString(string delimiter, bool showEditStepValues = false)
			{
				var parentType = state.parent?.GetType().GetNiceTypeName() ?? "null";
				var targetType = state.target?.GetType().GetNiceTypeName() ?? "null";
				var sb = new StringBuilder();
				if (showEditStepValues)
				{
					sb.AppendFormat("dataTypeName: {0}", dataTypeName)
						.AppendFormat("{0}fieldPath: {1}", delimiter, fieldPath)
						.AppendFormat("{0}valueRaw: {1}{0}", delimiter, valueRaw);
				}
				sb.AppendFormat("segment: {0}", state.pathSegment)
					.AppendFormat("{0}segmentIndex: {1}", delimiter, state.pathSegmentIndex)
					.AppendFormat("{0}segmentCount: {1}", delimiter, state.pathSegmentCount)
					.AppendFormat("{0}atEnd: {1}", delimiter, state.atEndOfPath)
					.AppendFormat("{0}op: {1}", delimiter, state.op)
					.AppendFormat("{0}parent: {1}", delimiter, parentType)
					.AppendFormat("{0}target: {1}", delimiter, targetType)
					.AppendFormat("{0}targetType: {1}", delimiter, state.targetType.GetNiceTypeName())
					.AppendFormat("{0}targetIndex: {1}", delimiter, state.targetIndex)
					.AppendFormat("{0}targetKey: {1}", delimiter, state.targetKey);
				return sb.ToString();
			}
			public override string ToString() => ToDelimitedString(pipeDelimiter);
		}
	}
}
