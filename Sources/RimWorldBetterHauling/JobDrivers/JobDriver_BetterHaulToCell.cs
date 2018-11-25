using Harmony;
using RimWorld;
using RimWorldRealFoW.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimWorldBetterHauling.JobDrivers {
	public class JobDriver_BetterHaulToCell : JobDriver_HaulToCell {
		private bool forbiddenInitially;

		private const int maxHaulContester = 5;

		private List<Thing> hauledThingsInInventory = new List<Thing>(50);

		private List<Thing> scheduledThingsToHaul = new List<Thing>(50);
		private List<int> scheduledStacksToHaul = new List<int>(50);


		private HashSet<Thing> discardedThings = new HashSet<Thing>();


		public override void Notify_Starting() {
			base.Notify_Starting();

			if (!hauledThingsInInventory.NullOrEmpty()) {
				Log.Error($"Started {this.GetType()} job with non empty inventory...");
				BetterHaulUtils.dropAllInventoryHauledThings(pawn, hauledThingsInInventory);
			}

			if (base.TargetThingA != null) {
				this.forbiddenInitially = base.TargetThingA.IsForbidden(this.pawn);
			} else {
				this.forbiddenInitially = false;
			}
		}

		public override bool TryMakePreToilReservations(bool errorOnFailed) {
			Pawn pawn = this.pawn;
			LocalTargetInfo target = this.job.GetTarget(TargetIndex.B);
			Job job = this.job;

			// Ensure to release all reservation: the TryMakePreToilReservations is called multiple times 
			// when opportunistic jobs are performed.
			pawn.Map.reservationManager.ReleaseClaimedBy(pawn, job);

			bool result;
			if (pawn.Reserve(target, job, 1, -1, null, errorOnFailed)) {
				IntVec3 destCell = target.Cell;

				target = this.job.GetTarget(TargetIndex.A);
				Thing carryThing = target.Thing;

				if (this.job.haulOpportunisticDuplicates) {
					// Reset the tracking collections.
					scheduledThingsToHaul.Clear();
					scheduledStacksToHaul.Clear();

					// Reset discarded thing buffer.
					discardedThings.Clear();

					// Reserve all haulable things.
					result = true;

					float maxMass = MassUtility.Capacity(pawn);

					int remainingCarryStack = pawn.carryTracker.AvailableStackSpace(carryThing.def);

					float haulMass = MassUtility.GearAndInventoryMass(pawn);

					Thing carriedThing = null;

					Thing haulableThing = target.Thing;
					Thing reservedThing = haulableThing;
					while (result && haulableThing != null) {
						int stackToReserve = 0;

						int currCarryStack = 0;
						float currStackMass = 0f;

						int nonReservedStack = BetterHaulUtils.getNonReservedStack(haulableThing, pawn);

						// Check carry.
						if ((carriedThing == null && pawn.carryTracker.innerContainer.CanAcceptAnyOf(haulableThing, true)) 
								|| (carriedThing != null && carriedThing.CanStackWith(haulableThing))) {
							stackToReserve = Math.Min(nonReservedStack, remainingCarryStack);
							if (stackToReserve > 0) {
								currCarryStack = stackToReserve;
							}
						}

						// Check inventory, if there is other to collect.
						if (stackToReserve < nonReservedStack && haulMass < maxMass) {
							int remainingStack = nonReservedStack - stackToReserve;
							float thingMass = haulableThing.GetStatValue(StatDefOf.Mass, true);
							int invStackSize = Math.Min(remainingStack, Mathf.FloorToInt((maxMass - haulMass) / thingMass));
							if (invStackSize > 0) {
								currStackMass = invStackSize * thingMass;
								if (haulMass + currStackMass < maxMass) {
									stackToReserve += invStackSize;
								}
							}
						}

						if (stackToReserve > 0) {
							if (pawn.Reserve(haulableThing, pawn.CurJob, maxHaulContester, stackToReserve, null, errorOnFailed && haulableThing == target.Thing)) {
								if (currCarryStack > 0 && carriedThing == null) {
									carriedThing = haulableThing;
								}

								haulMass += currStackMass;
								remainingCarryStack -= currCarryStack;
								
								reservedThing = haulableThing;
								if (haulableThing != target.Thing) {
									scheduledThingsToHaul.Add(haulableThing);
									scheduledStacksToHaul.Add(stackToReserve);
								} else {
									result = true;
									job.SetTarget(TargetIndex.A, haulableThing);
									job.count = stackToReserve;
								}

							} else {
								if (haulableThing != target.Thing) {
									this.discardedThings.Add(haulableThing);
								} else {
									result = false;
								}
							}
						} else {
							discardedThings.Add(haulableThing);
						}

						// Check same things.
						haulableThing = BetterHaulUtils.findHaulableThing(pawn, reservedThing.Position, destCell,
							(Thing t) => t != target.Thing
								&& !scheduledThingsToHaul.Contains(t)
								&& !discardedThings.Contains(t)
								&& t.def == reservedThing.def
								&& t.CanStackWith(reservedThing));

						if (haulableThing == null) {
							// Check other things of there no more of the same.
							haulableThing = BetterHaulUtils.findHaulableThing(pawn, reservedThing.Position, destCell,
								(Thing t) => t != target.Thing 
								&& !scheduledThingsToHaul.Contains(t)
								&& !discardedThings.Contains(t));
						}
					}
				} else {
					result = pawn.Reserve(carryThing, pawn.CurJob, maxHaulContester, BetterHaulUtils.getAllHaulableSize(carryThing, pawn), null, errorOnFailed);
				}
			} else {
				result = false;
			}

			discardedThings.Clear();
			return result;
		}

		public override void ExposeData() {
			base.ExposeData();

			Scribe_Collections.Look<Thing>(ref this.hauledThingsInInventory, "hauledThingsInInventory", LookMode.Reference);
			Scribe_Collections.Look<Thing>(ref this.scheduledThingsToHaul, "scheduledThingsToHaul", LookMode.Reference);
			Scribe_Collections.Look<int>(ref this.scheduledStacksToHaul, "scheduledStaksToHaul", LookMode.Value);
		}

		protected override IEnumerable<Toil> MakeNewToils() {
			Toil carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B)
				.FailOnDestroyedOrNull(TargetIndex.A);

			this.FailOnBurningImmobile(TargetIndex.B);
			
			this.AddFinishAction(delegate() {
				BetterHaulUtils.dropAllInventoryHauledThings(pawn, hauledThingsInInventory);
			});

			Toil reserveHaulable = defineReserveHaulableToil(TargetIndex.A, carryToCell)
				.FailOnDestroyedOrNull(TargetIndex.A);
			if (!this.forbiddenInitially) {
				reserveHaulable.FailOnForbidden(TargetIndex.A);
			}
			yield return reserveHaulable;

			Toil gotoToil = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
			// Handle dynamic reseletion before standard fail conditions.
			gotoToil.endConditions.Insert(0, delegate () {
				Job curJob = pawn.jobs.curJob;
				Thing thing = curJob.GetTarget(TargetIndex.A).Thing;
				if (curJob.haulMode == HaulMode.ToCellStorage) {
					IntVec3 cell = pawn.jobs.curJob.GetTarget(TargetIndex.B).Cell;
					if (thing.DestroyedOrNull()
							|| (!this.forbiddenInitially && thing.IsForbidden(pawn))
							|| Toils_Haul.ErrorCheckForCarry(pawn, thing)
							|| !BetterHaulUtils.isValidStorageAnywhereFor(cell, this.Map, thing)) {
						if (haulNext(reserveHaulable)) {
							return JobCondition.Ongoing;
						} else if (pawn.carryTracker.CarriedThing != null) {
							job.SetTarget(TargetIndex.A, pawn.carryTracker.CarriedThing);
							pawn.jobs.curDriver.JumpToToil(carryToCell);
							return JobCondition.Ongoing;
						}
						return JobCondition.Incompletable;
					}
				}
				return JobCondition.Ongoing;
			});
			gotoToil.FailOnSomeonePhysicallyInteracting(TargetIndex.A);
			yield return gotoToil; 

			yield return BetterHaulUtils.definePickThingToil(TargetIndex.A, hauledThingsInInventory);

			if (this.job.haulOpportunisticDuplicates) {
				// Check for remaining thing to haul.
				yield return new Toil() {
					initAction = delegate () {
						haulNext(reserveHaulable);
					}
				};
			}


			yield return carryToCell;

			Toil placeThing = Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true)
				.FailOnDestroyedOrNull(TargetIndex.A);
			Action placeThingOriginalInitAction = placeThing.initAction;
			placeThing.initAction = (delegate () {
				placeThingOriginalInitAction();

				// If not carrying anything, pop an inventory hauled thing.
				// It must be done immediately after placeing the old one to avoit fail action triggering.
				if (pawn.carryTracker.CarriedThing == null && hauledThingsInInventory.Count > 0) {
					Thing thing = hauledThingsInInventory.First();

					int initialStack = thing.stackCount;
					int availableSpace = this.pawn.carryTracker.AvailableStackSpace(thing.def);

					Thing resThing;
					int movedQuantity =
						this.pawn.inventory.innerContainer.TryTransferToContainer(thing, this.pawn.carryTracker.innerContainer, availableSpace, out resThing, true);
					if (initialStack == movedQuantity) {
						// Remove thing if everything has been moved from the inventory.
						hauledThingsInInventory.Remove(thing);
					}

					if (resThing == null || movedQuantity == 0) {
						throw new Exception("Error moving hauled thing to carry container = " + thing);
					}

					// Unreserve old thing, if any.
					BetterHaulUtils.releaseReservation(job.GetTarget(TargetIndex.A).Thing, pawn, job);

					// Set the moved thing as current target.
					this.job.SetTarget(TargetIndex.A, resThing);

					// If there is still something hauled, check if target cell is compatibile with the actual hauled thing, just repeat the placeing. 
					// Otherwise, find a new place cell.
					IntVec3 targetCell = pawn.jobs.curJob.GetTarget(TargetIndex.B).Cell;
					if (targetCell.IsValidStorageFor(this.Map, resThing)) {
						this.JumpToToil(placeThing);
					} else {
						if (StoreUtility.TryFindBestBetterStoreCellFor(resThing, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out targetCell, true)) {
							if (pawn.CanReserve(targetCell, 1, -1, null, false)) {
								pawn.Reserve(targetCell, pawn.CurJob, 1, -1, null, true);
							}

							BetterHaulUtils.releaseReservation(job.GetTarget(TargetIndex.B).Thing, pawn, job);
							pawn.CurJob.SetTarget(TargetIndex.B, targetCell);

							this.JumpToToil(carryToCell);
						} else {
							EndJobWith(JobCondition.Incompletable);
						}
					}
				}
			});
			yield return placeThing;

			yield break;
		}

		private bool haulNext(Toil nextHaulToil) {
			IntVec3 cell = pawn.jobs.curJob.GetTarget(TargetIndex.B).Cell;
			while (scheduledThingsToHaul.Count > 0) {
				Thing thing = scheduledThingsToHaul.First();
				int stackSize = scheduledStacksToHaul.First();

				scheduledThingsToHaul.RemoveAt(0);
				scheduledStacksToHaul.RemoveAt(0);

				if (!thing.DestroyedOrNull() && !Toils_Haul.ErrorCheckForCarry(pawn, thing) && BetterHaulUtils.isValidStorageAnywhereFor(cell, this.Map, thing)) {
					job.SetTarget(TargetIndex.A, thing);
					job.count = stackSize;
					pawn.jobs.curDriver.JumpToToil(nextHaulToil);

					return true;

				// No more valid for storage. Skipping and removing reservation if any.
				} else if (!thing.DestroyedOrNull()) {
					BetterHaulUtils.releaseReservation(thing, pawn, job);
				}
			}
			return false;
		}


		private Toil defineReserveHaulableToil(TargetIndex ind, Toil carryToCell) {
			Toil toil = new Toil();
			toil.initAction = delegate () {
				Thing thing = toil.actor.jobs.curJob.GetTarget(ind).Thing;
				if (!BetterHaulUtils.reserveHaulableStack(thing, toil.actor, toil.actor.CurJob, maxHaulContester)) {
					Log.Warning("Cannot reserve " + thing + " for " + toil.actor
						+ "; haulable" + BetterHaulUtils.getAllHaulableSize(thing, toil.actor) 
						+ "; job: " + toil.actor.CurJob.count
						+ "; unreserved: " + BetterHaulUtils.getNonReservedStack(thing, toil.actor)
						+ "; carr: " + BetterHaulUtils.getCarryeableSize(thing, toil.actor)
						+ "; inb: " + BetterHaulUtils.getInventoryeableSize(thing, toil.actor)
						+ "; mass: " + thing.GetStatValue(StatDefOf.Mass, true)
						+ "; available: " + MassUtility.FreeSpace(toil.actor)
						+ "; used: " + MassUtility.GearAndInventoryMass(toil.actor));

					// If the problem is the haulability, skip to next, if any, or start carrying.
					if (Math.Min(BetterHaulUtils.getAllHaulableSize(thing, pawn), job.count) <= 0) {
						// Skip.
						if (!thing.DestroyedOrNull()) {
							BetterHaulUtils.releaseReservation(thing, toil.actor, toil.actor.CurJob);
						}

						if (haulNext(toil)) {
							//Log.Message(" - to next thing");
						} else if (pawn.carryTracker.CarriedThing != null) {
							//Log.Message(" - to next carryng");

							job.SetTarget(TargetIndex.A, pawn.carryTracker.CarriedThing);
							pawn.jobs.curDriver.JumpToToil(carryToCell);
						} else {
							//Log.Message(" - cannot skip.. failing");

							toil.actor.jobs.EndCurrentJob(JobCondition.Incompletable, true);
						}
					} else {
						toil.actor.jobs.EndCurrentJob(JobCondition.Incompletable, true);
					}
				}
			};
			toil.atomicWithPrevious = true;
			return toil;
		}
		
		
	}
}

