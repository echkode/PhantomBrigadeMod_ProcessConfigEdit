﻿// Copyright (c) 2023 EchKode
// SPDX-License-Identifier: BSD-3-Clause

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
			var spec = new ModUtilities.EditSpec()
			{
				modIndex = i,
				modID = modID,
				filename = ModUtilities.FindConfigKeyIfEmpty(target, dataTypeName, filename),
				dataTypeName = dataTypeName,
				root = target,
				fieldPath = fieldPath,
				valueRaw = valueRaw,
			};
			if (ModLink.Settings.logging)
			{
				Debug.LogFormat(
					"Mod {0} ({1}) applying edit to config {2} path {3}",
					spec.modIndex,
					spec.modID,
					spec.filename,
					spec.fieldPath);
			}
			ModUtilities.ProcessFieldEdit(spec);
			return false;
		}
	}
}
