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

				Debug.LogFormat(
					"Mod {0} ({1}) no settings file found, using defaults | path: {2}",
					modIndex,
					modID,
					settingsPath);
			}

			if (Settings.logging)
			{
				Debug.LogFormat(
					"Mod {0} ({1}) diagnostic logging is on: {2}",
					modIndex,
					modID,
					Settings.logging);
			}
		}
	}
}
