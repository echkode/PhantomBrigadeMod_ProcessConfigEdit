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
			public bool logDiagnostics;
			public bool logContext;
			public bool logTagMap;
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
			Debug.LogFormat(
				"Mod {0} ({1}) settings | path: {2}\n  diagnostic logging: {3}\n  context logging: {4}\n  tag map logging: {5}",
				modIndex,
				modID,
				settingsPath,
				Settings.logDiagnostics ? "on" : "off",
				Settings.logContext ? "on" : "off",
				Settings.logTagMap ? "on" : "off");
		}
	}
}
