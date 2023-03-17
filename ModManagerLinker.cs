// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Generic;
using System.IO;
using System.Linq;

using PhantomBrigade.Data;
using PhantomBrigade.Mods;
using PBModManager = PhantomBrigade.Mods.ModManager;

using UnityEngine;

namespace EchKode.PBMods.ProcessConfigEdit
{
	static partial class ModManager
	{
		private const string configDirectoryPrefix = "Configs/";
		private const string modConfigEditsDirectoryPath = "ConfigEdits/";
		private const string modConfigOverridesDirectoryPath = "ConfigOverrides/";

		public static void Update<T>(
			int modIndex,
			string modID,
			string modPath,
			T data,
			bool logging = false)
			where T : DataContainerUnique, new()
		{
			var dataTypeStatic = typeof(T);
			var configPath = DataPathUtility.GetPath(dataTypeStatic);
			if (configPath == null)
			{
				Debug.LogWarningFormat(
					"Mod {0} ({1}) paths.yaml doesn't have an entry for {2}",
					modIndex,
					modID,
					dataTypeStatic.Name);
				return;
			}
			configPath = configPath.Substring(configDirectoryPrefix.Length);

			var (foundOverride, configOverridePath) = LoadConfigOverride(
				modID,
				modPath,
				dataTypeStatic,
				configPath);
			var (foundEdit, configEditPath) = LoadConfigEdit(
				modIndex,
				modID,
				modPath,
				dataTypeStatic,
				configPath);

			if (!foundOverride && !foundEdit)
			{
				Debug.LogWarningFormat(
					"Mod {0} ({1}) no config edit/override file found for {2} -- one should exist at either or both of the following paths\n  config override: {2}\n  config edit: {3}",
					modIndex,
					modID,
					dataTypeStatic.Name,
					configOverridePath,
					configEditPath);
				return;
			}

			ProcessConfigModsForLinker(modIndex, modID, data, logging);
			data.OnAfterDeserialization();
		}

		private static (bool, string) LoadConfigOverride(
			string modID,
			string modPath,
			System.Type dataTypeStatic,
			string configPath)
		{
			var loadedData = PBModManager.loadedModsLookup[modID];
			var yamlPath = configPath + ".yaml";
			var fileName = Path.GetFileName(yamlPath);
			var key = Path.GetFileName(configPath);
			var configOverridePath = modPath
				+ modConfigOverridesDirectoryPath
				+ configPath
				+ ".yaml";
			var replacement = UtilitiesYAML.ReadFromFile(dataTypeStatic, configOverridePath, false);
			if (replacement == null)
			{
				return (false, "");
			}

			if (loadedData.configEdits == null)
			{
				loadedData.configOverrides = new List<ModConfigOverride>();
			}

			loadedData.configOverrides.Add(new ModConfigOverride()
			{
				pathFull = configOverridePath,
				pathTrimmed = configPath,
				filename = fileName,
				key = key,
				typeName = dataTypeStatic.Name,
				type = dataTypeStatic,
				containerObject = replacement,
			});
			return (true, configOverridePath);
		}

		private static (bool, string) LoadConfigEdit(
			int modIndex,
			string modID,
			string modPath,
			System.Type dataTypeStatic,
			string configPath)
		{
			var loadedData = PBModManager.loadedModsLookup[modID];
			var yamlPath = configPath + ".yaml";
			var fileName = Path.GetFileName(yamlPath);
			var key = Path.GetFileName(configPath);
			var configEditPath = modPath
				+ modConfigEditsDirectoryPath
				+ configPath
				+ ".yaml";
			var configEditSerialized = UtilitiesYAML.ReadFromFile<ModConfigEditSerialized>(configEditPath, false);
			if (configEditSerialized == null || configEditSerialized.edits == null)
			{
				return (false, "");
			}

			var mce = new ModConfigEdit()
			{
				removed = configEditSerialized.removed,
				edits = new List<ModConfigEditStep>(configEditSerialized.edits.Count),
			};
			for (var i = 0; i < configEditSerialized.edits.Count; i += 1)
			{
				var edit = configEditSerialized.edits[i];
				if (edit == null)
				{
					continue;
				}

				var parts = edit.Split(':');
				if (parts.Length != 2)
				{
					Debug.LogWarningFormat(
						"Mod {0} ({1}) edit has invalid number of separators | path: {2}\n  line: {3}\n  {4}",
						modIndex,
						modID,
						configEditPath,
						i,
						edit);
					continue;
				}

				var fieldPath = parts[0];
				if (string.IsNullOrEmpty(fieldPath))
				{
					continue;
				}

				mce.edits.Add(new ModConfigEditStep()
				{
					path = fieldPath,
					value = parts[1].TrimStart(' '),
				});
			}

			if (loadedData.configEdits == null)
			{
				loadedData.configEdits = new List<ModConfigEditLoaded>();
			}

			loadedData.configEdits.Add(new ModConfigEditLoaded()
			{
				pathFull = configEditPath,
				pathTrimmed = configPath,
				filename = fileName,
				key = key,
				typeName = dataTypeStatic.Name,
				type = dataTypeStatic,
				data = mce,
			});
			return (true, configEditPath);
		}

		static void ProcessConfigModsForLinker<T>(
			int modIndex,
			string modID,
			T dataInternal,
			bool logging)
		  where T : DataContainerUnique, new()
		{
			if (dataInternal == null)
			{
				return;
			}
			if (PBModManager.config == null)
			{
				return;
			}
			if (!PBModManager.config.enabled)
			{
				return;
			}
			if (PBModManager.loadedMods == null)
			{
				return;
			}

			var loadedMod = PBModManager.loadedModsLookup[modID];
			var dataType = typeof(T);
			var spec = new EditSpec()
			{
				i = modIndex,
				modID = modID,
				dataTypeName = dataType.Name,
			};
			ApplyOverrides(spec, loadedMod, dataInternal, logging);
			ApplyEdits(spec, loadedMod, dataInternal, logging);
		}

		static void ApplyOverrides<T>(
			EditSpec spec,
			ModLoadedData loadedMod,
			T dataInternal,
			bool logging)
		  where T : DataContainerUnique, new()
		{
			if (loadedMod.configOverrides == null || loadedMod.configOverrides.Count == 0)
			{
				return;
			}

			var dataType = typeof(T);
			loadedMod.configOverrides
				.Where(o => o != null)
				.Where(o => o.containerObject != null)
				.Where(o => o.type == dataType)
				.ToList()
				.ForEach(configOverride =>
				{
					if (!(configOverride.containerObject is T containerObject))
					{
						return;
					}

					dataInternal = containerObject;
					if (logging)
					{
						Debug.LogFormat(
							"Mod {0} ({1}) replaces config {2} of type {3}",
							spec.i,
							spec.modID,
							configOverride.key,
							spec.dataTypeName);
					}
				});
		}

		static void ApplyEdits<T>(
			EditSpec spec,
			ModLoadedData loadedMod,
			T dataInternal,
			bool logging)
		  where T : DataContainerUnique, new()
		{
			if (loadedMod.configEdits == null || loadedMod.configEdits.Count == 0)
			{
				return;
			}

			var dataType = typeof(T);
			var editsCount = loadedMod.configEdits.Count;
			loadedMod.configEdits
				.Select((e, i) => new
				{
					Index = i,
					Edit = e,
				})
				.Where(ei => ei.Edit != null)
				.Where(ei => ei.Edit.data != null)
				.Where(ei => ei.Edit.data.edits != null)
				.Where(ei => ei.Edit.data.edits.Count != 0)
				.Where(ei => ei.Edit.type == dataType)
				.ToList()
				.ForEach(ei =>
				{
					if (logging)
					{
						Debug.LogFormat(
							"Mod {0} ({1}) applying edit script {2} of {3}: <key={4};type={5};filename={6}>",
							spec.i,
							spec.modID,
							ei.Index + 1,
							editsCount,
							ei.Edit.key,
							ei.Edit.typeName,
							ei.Edit.filename);
					}
					ApplyEdit(spec, dataInternal, ei.Edit, logging);
				});
		}

		static void ApplyEdit<T>(
			EditSpec spec,
			T dataInternal,
			ModConfigEditLoaded configEdit,
			bool logging)
		  where T : DataContainerUnique, new()
		{
			var key = configEdit.key;
			var data = configEdit.data;
			if (data.removed)
			{
				Debug.LogWarningFormat(
					"Mod {0} ({1}) attempts to removes global config which is not allowed | key: {2} | type {3}",
					spec.i,
					spec.modID,
					key,
					spec.dataTypeName);
				return;
			}

			if (logging)
			{
				Debug.LogFormat(
					"Mod {0} ({1}) edits config {2} of type {3}",
					spec.i,
					spec.modID,
					key,
					spec.dataTypeName);
			}
			spec.root = dataInternal;
			spec.filename = key;
			var editsCount = data.edits.Count;
			data.edits
				.Select((e, i) => new
				{
					Index = i,
					Edit = e,
				})
				.ToList()
				.ForEach(ei =>
				{
					if (logging)
					{
						Debug.LogFormat(
							"Mod {0} ({1}) applying edit {2} of {3} to config {4}",
							spec.i,
							spec.modID,
							ei.Index + 1,
							editsCount,
							key);
					}
					spec.fieldPath = ei.Edit.path;
					spec.valueRaw = ei.Edit.value;
					ProcessFieldEdit(spec);
				});
		}
	}
}
