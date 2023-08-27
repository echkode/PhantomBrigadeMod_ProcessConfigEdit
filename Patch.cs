// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Generic;

using HarmonyLib;

using PBModUtilities = PhantomBrigade.Mods.ModUtilities;

using UnityEngine;

namespace EchKode.PBMods.ProcessConfigEdit
{
	[HarmonyPatch]
	static class Patch
	{
		[HarmonyPatch(typeof(PBModUtilities), "ProcessFieldEdit", new System.Type[]
		{
			typeof(object),
			typeof(string),
			typeof(string),
			typeof(string),
			typeof(int),
			typeof(string),
			typeof(string),
		})]
		[HarmonyPrefix]
		static bool Mu_ProcessFieldEditPrefix(
			object target,
			string filename,
			string fieldPath,
			string valueRaw,
			int i,
			string modID,
			string dataTypeName)
		{
			var resolvedFilename = ModUtilities.FindConfigKeyIfEmpty(target, dataTypeName, filename);
			if (resolvedFilename != lastFilename)
			{
				lastFilename = resolvedFilename;
				lastFaulted = false;
				pathContexts.Clear();
			}
			else if (lastFaulted)
			{
				return false;
			}

			var spec = new ModUtilities.EditSpec()
			{
				modIndex = i,
				modID = modID,
				filename = resolvedFilename,
				dataTypeName = dataTypeName,
				root = target,
				fieldPath = fieldPath,
				valueRaw = valueRaw,
				pathContexts = pathContexts,
			};
			if (ModLink.Settings.logging)
			{
				Debug.LogFormat(
					"Mod {0} ({1}) applying edit to config {2} path {3}",
					spec.modIndex,
					spec.modID,
					spec.filename,
					ModUtilities.ReplacePathContextInFieldPath(spec));
			}
			ModUtilities.ProcessFieldEdit(spec);
			lastFaulted = spec.state.faulted;
			return false;
		}

		private static string lastFilename;
		private static bool lastFaulted;
		private static List<ModUtilities.PathContext> pathContexts = new List<ModUtilities.PathContext>();
	}
}
