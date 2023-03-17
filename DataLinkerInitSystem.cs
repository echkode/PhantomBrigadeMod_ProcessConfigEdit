// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using PhantomBrigade.Data;

namespace EchKode.PBMods.ProcessConfigEdit
{
	static class DataLinkerInitSystem
	{
		public static void Initialize()
		{
			ModManager.Update(
				ModLink.modIndex,
				ModLink.modID,
				ModLink.modPath,
				DataShortcuts.ui,
				ModLink.Settings.logging);
		}
	}
}
