// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using System.IO;

using UnityEngine;

namespace EchKode.PBMods.ProcessConfigEdit
{
	partial class ModLink
	{
		internal sealed class ModSettings
		{
#pragma warning disable CS0649
			public bool logging;
			public bool logContext;
#pragma warning restore CS0649
		}

		internal static ModSettings Settings;

		static void LoadSettings()
		{
			var settingsPath = Path.Combine(modPath, "settings.yaml");
			Settings = UtilitiesYAML.ReadFromFile<ModSettings>(settingsPath, false);
			if (Settings == null)
			{
				Settings = new ModSettings();
			}
			else
			{
				Debug.LogFormat(
					"Mod {0} ({1}) settings file found | path: {2}",
					modIndex,
					modID,
					settingsPath);
			}

			if (Settings.logging)
			{
				Debug.LogFormat(
					"Mod {0} ({1}) diagnostic logging is on | context logging: {2}",
					modIndex,
					modID,
					Settings.logContext ? "on" : "off");
			}
		}
	}
}
