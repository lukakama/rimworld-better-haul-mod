using Harmony;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimWorldRealFoW.Utils {
	class BetterHaulUtils {
		public static void dropAllInventoryHauledThings(Pawn pawn, List<Thing> hauledThingsInInventory) {
			foreach (var thingToDrop in hauledThingsInInventory) {
				// Ensure that all hauled thing are dropped somewhere.
				Thing resThing;
				if (!pawn.inventory.innerContainer.TryDrop(thingToDrop, pawn.Position, pawn.Map, ThingPlaceMode.Near, out resThing)) {
					Log.Error(string.Concat(new object[]
						{
							"Incomplete haul for ",
							pawn,
							": Could not find anywhere to drop ",
							thingToDrop,
							" near ",
							pawn.Position,
							". Destroying. This should never happen!"
						}), false);
					thingToDrop.Destroy(DestroyMode.Vanish);
				} else if (resThing != null) {
					// If something dropped.

					// As Vanilla, forbid thing dropped from hostiles.
					if (pawn.Faction.HostileTo(Faction.OfPlayer)) {
						resThing.SetForbidden(true, false);
					} else {
						// Thing hauled in the inventory got all the designator removed. If the thing needs the haul designator, red-add it.
						// TODO: find a way to track all the designations in order to re-add them.
						if (resThing.def.designateHaulable) {
							Designator_Haul haulDesignator = Find.ReverseDesignatorDatabase.Get<Designator_Haul>();
							if (haulDesignator.CanDesignateThing(resThing).Accepted) {
								haulDesignator.DesignateThing(resThing);
							}
						}
					}
				}
			}
			hauledThingsInInventory.Clear();
		}

		public static void releaseReservation(Thing thing, Pawn pawn, Job job) {
			if (!thing.DestroyedOrNull() && pawn.Map.reservationManager.ReservedBy(thing, pawn, job)) {
				pawn.Map.reservationManager.Release(thing, pawn, job);
			}
		}

		public static Toil definePickThingToil(TargetIndex haulableInd, List<Thing> hauledInInventory, bool failIfUnavailableCount = false) {
			Toil toil = new Toil();
			toil.initAction = delegate () {
				Pawn actor = toil.actor;
				Job curJob = actor.jobs.curJob;

				Thing thing = curJob.GetTarget(haulableInd).Thing;
				if (thing.DestroyedOrNull() || Toils_Haul.ErrorCheckForCarry(actor, thing)) {
					// Skip.
					if (!thing.DestroyedOrNull()) {
						BetterHaulUtils.releaseReservation(thing, actor, curJob);
					}
					return;
				}

				int initialStack = thing.stackCount;

				int nonReservedStack = Math.Min(curJob.count, getNonReservedStack(thing, actor));

				if (failIfUnavailableCount && nonReservedStack != curJob.count) {
					actor.jobs.EndCurrentJob(JobCondition.Incompletable, true);
				}

				Thing pickedThing = null;

				int pickedCount = 0;

				// Check carryeable quantity.
				int pickeableCount = Math.Min(getCarryeableSize(thing, actor), nonReservedStack);
				if (pickeableCount > 0) {
					pickedCount = actor.carryTracker.TryStartCarry(thing, pickeableCount, false);
					if (pickedCount > 0) {
						pickedThing = actor.carryTracker.CarriedThing;

						BetterHaulUtils.releaseReservation(pickedThing, actor, curJob);
						actor.Reserve(pickedThing, actor.CurJob, 1, -1, null, true);
					} else {
						Log.Warning("empty picking thing " + thing);
						actor.jobs.EndCurrentJob(JobCondition.Incompletable, true);
					}
				}

				// Check if the remainings are pickeable in the inventory.
				if (pickedCount < nonReservedStack) {
					pickeableCount = Math.Min(getInventoryeableSize(thing, actor), nonReservedStack - pickedCount);

					if (pickeableCount > 0) {
						Thing thingToPick = thing.SplitOff(pickeableCount);

						if (actor.inventory.GetDirectlyHeldThings().TryAdd(thingToPick, false)) {
							hauledInInventory.Add(thingToPick);

							pickedCount += thingToPick.stackCount;

							if (pickedThing == null) {
								thingToPick.def.soundPickup.PlayOneShot(new TargetInfo(thingToPick.Position, actor.Map, false));
							}
							pickedThing = thingToPick;

							BetterHaulUtils.releaseReservation(pickedThing, actor, curJob);
							actor.Reserve(pickedThing, actor.CurJob, 1, -1, null, true);
						} else {
							Log.Warning("empty picking thing " + thing);
							actor.jobs.EndCurrentJob(JobCondition.Incompletable, true);
						}
					}
				}

				if (pickeableCount <= 0) {
					throw new Exception("Tried to pick an unpickeable = " + pickeableCount);
				}

				if ((initialStack != pickedCount) && !thing.DestroyedOrNull()) {
					// Unreserve any non pickeable stack.
					BetterHaulUtils.releaseReservation(thing, actor, curJob);
				}

				// Set the last pickedThing as main target and increment the hauled things counter.
				curJob.SetTarget(haulableInd, actor.carryTracker.CarriedThing);

				actor.records.Increment(RecordDefOf.ThingsHauled);
			};
			return toil;
		}



		public static int getNonReservedStack(Thing thing, Pawn pawn) {
			List<ReservationManager.Reservation> reservations =
				Traverse.Create(thing.Map.reservationManager).Field("reservations").GetValue<List<ReservationManager.Reservation>>();

			int nonReservedStack = thing.stackCount;
			for (int i = 0; i < reservations.Count; i++) {
				ReservationManager.Reservation reservation = reservations[i];
				if ((reservation.Target.Thing == thing) && reservation.Layer == null) {
					if (reservation.Claimant != pawn) {
						if (reservation.StackCount == -1) {
							return 0;
						} else {
							nonReservedStack -= reservation.StackCount;
						}
					}
				}
			}

			if (nonReservedStack < 0) {
				Log.Warning("Detected reserved count greater than the full stack for " + thing);
			}

			return Math.Max(nonReservedStack, 0);
		}

		public static int getCarryeableSize(Thing thing, Pawn pawn) {
			if (pawn.carryTracker.innerContainer.CanAcceptAnyOf(thing, true)) {
				return Math.Min(thing.stackCount, pawn.carryTracker.AvailableStackSpace(thing.def));
			}
			return 0;
		}

		public static int getInventoryeableSize(Thing thing, Pawn pawn) {
			return Mathf.Min(new int[]
				{
					MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing),
					thing.stackCount
				});
		}

		public static int getAllHaulableSize(Thing thing, Pawn pawn) {
			int stackCount = Math.Max(getCarryeableSize(thing, pawn), 0);

			// Inventory should remove carryeable quantity, normalizing at 0 if the inventory 
			// can take less than carry.
			stackCount += Math.Max(getInventoryeableSize(thing, pawn) - stackCount, 0);

			// Remove quantity reservanle by other pawns.
			return Math.Min(stackCount, getNonReservedStack(thing, pawn));
		}

		public static bool reserveHaulableStack(Thing thing, Pawn pawn, Job job, int maxHaulContester) {
			int stackCount = Math.Min(getAllHaulableSize(thing, pawn), job.count);
			if (stackCount > 0) {
				return pawn.Reserve(thing, pawn.CurJob, maxHaulContester, stackCount);
			}

			return false;
		}

		public static Thing findHaulableThing(Pawn actor, IntVec3 position, IntVec3 targetCell, Predicate<Thing> extraValidator = null) {
			Predicate<Thing> validator = (Thing t) =>
				t.Spawned &&
				!t.IsForbidden(actor) &&
				!t.IsInValidStorage() &&
				isValidStorageAnywhereFor(targetCell, actor.Map, t) &&
				(extraValidator == null || extraValidator(t));

			return GenClosest.ClosestThing_Global_Reachable(position, actor.Map, actor.Map.listerHaulables.ThingsPotentiallyNeedingHauling(),
				PathEndMode.ClosestTouch, TraverseParms.For(actor, Danger.Deadly, TraverseMode.ByPawn, false), 8f, validator, null);

			/*
			return GenClosest.ClosestThingReachable(position, actor.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableAlways),
				PathEndMode.ClosestTouch, TraverseParms.For(actor, Danger.Deadly, TraverseMode.ByPawn, false), 8f, validator, null, 0, -1, false,
				RegionType.Set_Passable, false);
			*/
		}

		public static bool isValidStorageAnywhereFor(IntVec3 c, Map map, Thing storable) {
			SlotGroup slotGroup = c.GetSlotGroup(map);
			return slotGroup != null && slotGroup.parent.Accepts(storable);
		}
	}
}
