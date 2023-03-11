using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using HarmonyLib;

using PhantomBrigade.Data;

using QFSW.QC;

namespace EchKode.PBMods.ProcessConfigEdit.ConsoleCommands
{
	using CommandList = List<(string QCName, string Description, MethodInfo Method)>;

	static partial class EquipmentInspector
	{
		internal static CommandList Commands() => new CommandList()
		{
			("eq.save-part-preset", "Save a part preset to a YAML file", AccessTools.DeclaredMethod(typeof(EquipmentInspector), nameof(SavePartPreset), new Type[] { typeof(string) })),
			("eq.save-part-preset", "Save a part preset to a YAML file", AccessTools.DeclaredMethod(typeof(EquipmentInspector), nameof(SavePartPreset), new Type[] { typeof(string), typeof(string) })),
		};

		static void SavePartPreset(string key) => SavePartPreset(key, ModLink.modPath);

		static void SavePartPreset(string key, string savePath)
		{
			var part = DataMultiLinkerPartPreset.GetEntry(key);
			if (part == null)
			{
				QuantumConsole.Instance.LogToConsole("Part preset not found");
				return;
			}
			if (Directory.Exists(savePath))
			{
				savePath = Path.Combine(savePath, key + ".yaml");
			}
			UtilitiesYAML.SaveToFile(savePath, part);
		}
	}
}
