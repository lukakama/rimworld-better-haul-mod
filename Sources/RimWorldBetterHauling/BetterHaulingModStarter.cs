//   Copyright 2017 Luca De Petrillo
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
using Harmony;
using RimWorld;
using RimWorldBetterHauling.JobDrivers;
using System;
using System.Linq;
using System.Reflection;

#if InternalProfile
using RimWorldRealFoW.Detours.Profiling;
#endif
#if Profile
using System.Runtime.InteropServices;
#endif
using Verse;
using Verse.AI;

namespace RimWorldBetterHauling {
	[StaticConstructorOnStartup]
	public class BetterHaulingModStarter : Mod {

#if Profile
		[DllImport("__Internal")]
		private static extern void mono_profiler_load(string args);
#endif

		static HarmonyInstance harmony;
		static BetterHaulingModStarter() {

#if Profile
			mono_profiler_load(@"default:time,file=d:/rimworld-prof.mprf");
#endif

			harmony = HarmonyInstance.Create("com.github.lukakama.rimworldmodbetterhauling");
			injectDetours();
			harmony = null;
		}

		public BetterHaulingModStarter(ModContentPack content) : base(content) {
			LongEventHandler.QueueLongEvent(injectComponents, "Better Hauling - Init.", false, null);
		}

		public static void injectComponents() {
			JobDefOf.HaulToCell.driverClass = typeof(JobDriver_BetterHaulToCell);
			JobDefOf.DoBill.driverClass = typeof(JobDriver_BetterDoBill);
		}

		public static void injectDetours() {
#if InternalProfile
			// Profiling
			patchMethod(typeof(EditWindow_DebugInspector), typeof(_EditWindow_DebugInspector), "CurrentDebugString");
			patchMethod(typeof(TickManager), typeof(_TickManager), "DoSingleTick");
#endif
		}

		public static void patchMethod(Type sourceType, Type targetType, string methodName) {
			patchMethod(sourceType, targetType, methodName, null);
		}

		public static void patchMethod(Type sourceType, Type targetType, string methodName, params Type[] types) {
			MethodInfo method = null;
			if (types != null) {
				method = sourceType.GetMethod(methodName, GenGeneric.BindingFlagsAll, null, types, null);
			} else {
				method = sourceType.GetMethod(methodName, GenGeneric.BindingFlagsAll);
			}

			if (method != null) {
				MethodInfo newMethodPrefix = null;
				if (types != null) {
					newMethodPrefix = targetType.GetMethod(methodName + "_Prefix", GenGeneric.BindingFlagsAll, null, types, null);
					if (newMethodPrefix == null) {
						newMethodPrefix = targetType.GetMethod(methodName + "_Prefix", GenGeneric.BindingFlagsAll, null, (new Type[] { sourceType }).Concat(types).ToArray(), null);
					}
				}
				if (newMethodPrefix == null) {
					newMethodPrefix = targetType.GetMethod(methodName + "_Prefix", GenGeneric.BindingFlagsAll);
				}

				MethodInfo newMethodPostfix = null;
				if (types != null) {
					newMethodPostfix = targetType.GetMethod(methodName + "_Postfix", GenGeneric.BindingFlagsAll, null, types, null);
					if (newMethodPostfix == null) {
						newMethodPostfix = targetType.GetMethod(methodName + "_Postfix", GenGeneric.BindingFlagsAll, null, (new Type[] { sourceType }).Concat(types).ToArray(), null);
					}
				}
				if (newMethodPostfix == null) {
					newMethodPostfix = targetType.GetMethod(methodName + "_Postfix", GenGeneric.BindingFlagsAll);
				}

				if (newMethodPrefix != null || newMethodPostfix != null) {
					if (patchWithHarmony(method, newMethodPrefix, newMethodPostfix)) {
						Log.Message("Patched method " + method.ToString() + " from source " + sourceType + " to " + targetType + ".");
					} else {
						Log.Warning("Unable to patch method " + method.ToString() + " from source " + sourceType + " to " + targetType + ".");
					}
				} else {
					Log.Warning("Target method prefix or suffix " + methodName + " not found for patch from source " + sourceType + " to " + targetType + ".");
				}
			} else {
				Log.Warning("Source method " + methodName + " not found for patch from source " + sourceType + " to " + targetType + ".");
			}
		}

		public static bool patchWithHarmony(MethodInfo original, MethodInfo prefix, MethodInfo postfix) {
			try {
				HarmonyMethod harmonyPrefix = prefix != null ? new HarmonyMethod(prefix) : null;
				HarmonyMethod harmonyPostfix = postfix != null ? new HarmonyMethod(postfix) : null;

				harmony.Patch(original, harmonyPrefix, harmonyPostfix);

				return true;
			} catch (Exception ex) {
				Log.Warning("Error patching with Harmony: " + ex.Message);
				Log.Warning(ex.StackTrace);
				return false;
			}
		}
	}
}
