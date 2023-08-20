// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using System;

using HarmonyLib;

using PBModManager = PhantomBrigade.Mods.ModManager;

using UnityEngine;

namespace EchKode.PBMods.ProcessConfigEdit
{
	public partial class ModLink : PhantomBrigade.Mods.ModLink
	{
		internal static int modIndex;
		internal static string modID;
		internal static string modPath;

		public override void OnLoad(Harmony harmonyInstance)
		{
			// Uncomment to get a file on the desktop showing the IL of the patched methods.
			// Output from FileLog.Log() will trigger the generation of that file regardless if this is set so
			// FileLog.Log() should be put in a guard.
			//Harmony.DEBUG = true;

			modIndex = PBModManager.loadedMods.Count;
			modID = metadata.id;
			modPath = metadata.path;

			LoadSettings();

			var patchAssembly = typeof(ModLink).Assembly;
			Debug.LogFormat(
				"Mod {0} ({1}) is executing OnLoad | Using HarmonyInstance.PatchAll on assembly ({2}) | Directory: {3} | Full path: {4}",
				modIndex,
				modID,
				patchAssembly.FullName,
				metadata.directory,
				metadata.path);
			harmonyInstance.PatchAll(patchAssembly);

			if (Harmony.DEBUG)
			{
				FileLog.Log($"{new string('=', 20)} Start [{DateTime.Now:u}] {new string('=', 20)}");
				FileLog.Log("!!! PBMods patches applied");
			}

			ModUtilities.Initialize();
			ConsoleCommands.Loader.RegisterCommands();

			Debug.LogFormat(
				"Mod {0} ({1}) is initialized",
				modIndex,
				modID);
		}
	}
}
