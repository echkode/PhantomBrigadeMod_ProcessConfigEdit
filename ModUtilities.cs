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
		}

		internal partial class EditSpec
		{
			public string dataTypeName;
			public object root;
			public string filename;
			public string fieldPath;
			public string valueRaw;
			public int modIndex;
			public string modID;
			public EditState state = new EditState();
		}

		internal class EditState
		{
			public object target;
			public EditOperation op;
			public int pathSegmentCount;
			public int pathSegmentIndex;
			public string pathSegment;
			public bool atEndOfPath;
			public int targetIndex;
			public object parent;
			public object targetKey;
			public FieldInfo fieldInfo;
			public Type targetType;
		}

		private static class Constants
		{
			public static class Operator
			{
				public const string Insert = "!+";
				public const string Remove = "!-";
				public const string DefaultValue = "!d";
				public const string NullValue = "!n";
			}
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

		private static HashSet<Type> allowedKeyTypes = new HashSet<Type>()
		{
			typeof(string),
			typeof(int),
		};

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

			operationMap = new Dictionary<string, EditOperation>()
			{
				[Constants.Operator.Insert] = EditOperation.Insert,
				[Constants.Operator.Remove] = EditOperation.Remove,
				[Constants.Operator.DefaultValue] = EditOperation.DefaultValue,
				[Constants.Operator.NullValue] = EditOperation.NullValue,
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
				ReportWarning(
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

			spec.state.target = spec.root;
			spec.state.parent = spec.root;
			spec.state.targetIndex = -1;
			spec.state.targetKey = null;
			spec.state.fieldInfo = null;
			spec.state.targetType = null;

			if (!WalkFieldPath(spec))
			{
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
					ReportWarning(
						spec,
						"attempts to edit",
						"Value type {0} cannot be set to null",
						spec.state.targetType.GetNiceTypeName());
					return;
				}

				spec.state.fieldInfo.SetValue(spec.state.parent, null);
				Report(
					spec,
					"edits",
					"Assigning null to target field");
				return;
			}

			if (updaterMap.TryGetValue(spec.state.targetType, out var updater))
			{
				updater(spec, update);
				return;
			}

			if (spec.state.op == EditOperation.Overwrite)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Value type {0} has no string parsing implementation -- try using {1} keyword if you're after filling it with default instance",
					spec.state.targetType.GetNiceTypeName(),
					Constants.Operator.DefaultValue);
				return;
			}

			if (spec.state.op != EditOperation.DefaultValue)
			{
				ReportWarning(
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
					ReportWarning(
						spec,
						"attempts to edit",
						"There is no type associated with tag {0}",
						valueRaw);
					return;
				}
				if (!spec.state.targetType.IsAssignableFrom(instanceType))
				{
					ReportWarning(
						spec,
						"attempts to edit",
						"Tag type {0} is not compatible with field type {1} | tag: {2}",
						instanceType.GetNiceTypeName(),
						spec.state.targetType.GetNiceTypeName(),
						valueRaw);
					return;
				}
			}

			if (spec.state.targetIndex != -1)
			{
				var list = (IList)spec.state.parent;
				list[spec.state.targetIndex] = Activator.CreateInstance(instanceType);
				Report(
					spec,
					"edits",
					"Assigning new default object of type {0} to target index {1}",
					instanceType.GetNiceTypeName(),
					spec.state.targetIndex);
				return;
			}

			if (spec.state.targetKey != null)
			{
				var map = (IDictionary)spec.state.parent;
				map[spec.state.targetKey] = Activator.CreateInstance(instanceType);
				Report(
					spec,
					"edits",
					"Assigning new default object of type {0} to target key {1}",
					instanceType.GetNiceTypeName(),
					spec.state.targetKey);
				return;
			}

			if (spec.state.fieldInfo == null)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"no target field info -- WalkFieldPath() failed to terminate properly | {0}",
					spec);
				return;
			}

			var instance = Activator.CreateInstance(instanceType);
			spec.state.fieldInfo.SetValue(spec.state.parent, instance);
			Report(
				spec,
				"edits",
				"Assigning new default object of type {0} to target field",
				instanceType.GetNiceTypeName());
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
			var pathSegments = spec.fieldPath.Split('.');
			spec.state.pathSegmentCount = pathSegments.Length;

			for (var i = 0; i < pathSegments.Length; i += 1)
			{
				spec.state.pathSegmentIndex = i;
				spec.state.pathSegment = pathSegments[i];
				spec.state.atEndOfPath = spec.state.pathSegmentIndex == spec.state.pathSegmentCount - 1;

				if (spec.state.target == null)
				{
					ReportWarning(
						spec,
						"attempts to edit",
						"Can't proceed past {0} (I{1} S{2}/{3}) -- current target reference is null",
						spec.state.pathSegment,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
					return false;
				}

				spec.state.targetType = spec.state.target.GetType();
				var child = i > 0;
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
			if (!int.TryParse(spec.state.pathSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 0)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Index {0} (I{1} S{2}/{3}) can't be parsed or is negative",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				return false;
			}

			var listType = list.GetType();
			var elementType = listType.IsArray
				? listType.GetElementType()
				: listType.GetGenericArguments()[0];
			if (spec.state.atEndOfPath)
			{
				if (!EditList(spec, list, result, elementType))
				{
					return false;
				}
			}
			else if (result >= list.Count)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Can't proceed past {0} (I{1} S{2}/{3}) -- current target reference is beyond end of list (size={4})",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount,
					list.Count);
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.fieldInfo = null;
			spec.state.targetIndex = result;
			spec.state.targetKey = null;
			spec.state.target = list[result];
			spec.state.targetType = elementType;

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
					ReportWarning(
						spec,
						"attempts to edit",
						"Default value for list insert is null (I{0} S{1}/{2}) -- likely missing a YAML tag",
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
					return false;
				}

				if (outOfBounds)
				{
					list.Add(instance);
					Report(
						spec,
						"edits",
						"Adding new entry of type {0} to end of the list (I{1} S{2}/{3})",
						elementType.GetNiceTypeName(),
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
				}
				else
				{
					list.Insert(index, instance);
					Report(
						spec,
						"edits",
						"Inserting new entry of type {0} to index {1} of the list (I{2} S{3}/{4})",
						elementType.GetNiceTypeName(),
						index,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
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
					ReportWarning(
						spec,
						"attempts to edit",
						"Index {0} (I{1} S{2}/{3}) can't be removed as it's out of bounds for list size {4}",
						spec.state.pathSegment,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount,
						list.Count);
					return false;
				}

				list.RemoveAt(index);
				Report(
					spec,
					"edits",
					"Removing entry at index {0} of the list (I{1} S{2}/{3})",
					index,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				return false;
			}

			if (outOfBounds)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Index {0} (I{1} S{2}/{3}) can't be replaced as it's out of bounds for list size {4}",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount,
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
				Report(
					spec,
					"attempts to edit",
					"Unable to produce map entry (I{0} S{1}/{2}) - only keys of types [{3}] are supported",
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount,
					permittedTypes);
				return false;
			}

			var key = spec.state.pathSegment;
			var (keyOK, resolvedKey) = ResolveTargetKey(map.GetType(), key);
			if (!keyOK)
			{
				Report(
					spec,
					"attempts to edit",
					"Unable to produce map entry for key {0} (I{1} S{2}/{3}) -- key can't be coerced to the correct type",
					key,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				return false;
			}
			var entryExists = map.Contains(resolvedKey);

			if (spec.state.atEndOfPath)
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
				ReportWarning(
					spec,
					"attempts to edit",
					"Can't proceed past {0} (I{1} S{2}/{3}), current target reference doesn't exist in dictionary)",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.fieldInfo = null;
			spec.state.targetIndex = -1;
			spec.state.targetKey = resolvedKey;
			spec.state.target = map[key];
			spec.state.targetType = valueType;

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
						ReportWarning(
							spec,
							"attempts to edit",
							"Default value for insert with key {0} is null (I{1} S{2}/{3}) -- likely missing a YAML tag",
							key,
							spec.state.pathSegmentIndex,
							spec.state.pathSegmentIndex + 1,
							spec.state.pathSegmentCount);
						return false;
					}
					map.Add(key, instance);
					Report(
						spec,
						"edits",
						"Adding key {0} (I{1} S{2}/{3}) to target dictionary",
						key,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
				}
				else
				{
					Report(
						spec,
						"attempts to edit",
						"Key {0} already exists, ignoring the command to add it",
						key);
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
					ReportWarning(
						spec,
						"attempts to edit",
						"Key {0} (I{1} S{2}/{3}) can't be removed from target dictionary -- it can't be found",
						key,
						spec.state.pathSegmentIndex,
						spec.state.pathSegmentIndex + 1,
						spec.state.pathSegmentCount);
					return false;
				}

				Report(
					spec,
					"edits",
					"Removing key {0} (I{1} S{2}/{3}) from target dictionary",
					key,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				map.Remove(key);
				return false;
			}

			return true;
		}

		private static bool ProduceField(EditSpec spec)
		{
			var field = spec.state.targetType.GetField(spec.state.pathSegment);
			if (field == null)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Field {0} (I{1} S{2}/{3}) could not be found on type {4}",
					spec.state.pathSegment,
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount,
					spec.state.targetType.GetNiceTypeName());
				return false;
			}

			spec.state.parent = spec.state.target;
			spec.state.fieldInfo = field;
			spec.state.targetIndex = -1;
			spec.state.targetKey = null;
			spec.state.target = field.GetValue(spec.state.target);
			spec.state.targetType = field.FieldType;

			return true;
		}

		private static (bool, Action<object>) ValidateEditState(EditSpec spec)
		{
			if (spec.state.parent == null)
			{
				ReportWarning(
					spec,
					"attempts to edit",
					"Arrived at a null parent after walking field path (I{0} S{1}/{2})",
					spec.state.pathSegmentIndex,
					spec.state.pathSegmentIndex + 1,
					spec.state.pathSegmentCount);
				return (false, null);
			}

			var parentType = spec.state.parent.GetType();
			var parentIsList = typeIList.IsAssignableFrom(parentType);
			if (parentIsList)
			{
				if (spec.state.targetIndex == -1)
				{
					ReportWarning(
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
					ReportWarning(
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
				ReportWarning(
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
					ReportWarning(
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
					ReportWarning(
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
					ReportWarning(
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
					ReportWarning(
						spec,
						"attempts to edit",
						"Can't parse raw value {0} -- {1}",
						spec.valueRaw,
						ex.Message);
					return;
				}
			}

			update(v);
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
				ReportWarning(
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
				ReportWarning(
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
					ReportWarning(
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
				ReportWarning(
					spec,
					"attempts to edit",
					"No addition or removal keywords detected -- no other operations are supported on hashsets");
				return;
			}

			if (spec.state.op == EditOperation.DefaultValue)
			{
				if (spec.state.target != null)
				{
					ReportWarning(
						spec,
						"attempts to edit",
						"Hashset exists -- cannot replace with default value");
					return;
				}

				spec.state.fieldInfo.SetValue(spec.state.parent, new HashSet<string>());
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
					ReportWarning(
						spec,
						"attempts to edit",
						"Enum field can't be overwritten -- can't parse raw value | type: {0} | value: {1}",
						targetType.GetNiceTypeName(),
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
			if (ModLink.Settings.logging)
			{
				var fixedFields = new object[]
				{
					spec.modIndex,
					spec.modID,
					verb,
					spec.filename,
					spec.dataTypeName,
					spec.fieldPath,

				};
				fmt = reFormatFieldSpecifier.Replace(fmt, RenumberSpecifier);
				Debug.LogFormat("Mod {0} ({1}) {2} config {3} of type {4}, field {5} | " + fmt, fixedFields.Concat(args).ToArray());
			}

			string RenumberSpecifier(Match m)
			{
				var i = int.Parse(m.Value.Substring(1, m.Value.Length - 2)) + 6;
				return "{" + i + "}";
			}
		}

		private static void ReportWarning(EditSpec spec, string verb, string fmt, params object[] args)
		{
			var fixedFields = new object[]
			{
				spec.modIndex,
				spec.modID,
				verb,
				spec.filename,
				spec.dataTypeName,
				spec.fieldPath,
			};
			fmt = reFormatFieldSpecifier.Replace(fmt, RenumberSpecifier);
			Debug.LogWarningFormat("Mod {0} ({1}) {2} config {3} of type {4} | field: {5} | " + fmt, fixedFields.Concat(args).ToArray());

			string RenumberSpecifier(Match m)
			{
				var i = int.Parse(m.Value.Substring(1, m.Value.Length - 2)) + 6;
				return "{" + i + "}";
			}
		}

		private static Regex reFormatFieldSpecifier = new Regex(@"\{\d+\}");

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

