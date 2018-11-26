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
	public class JobDriver_BetterDoBill : JobDriver_DoBill {
		private const int maxHaulContester = 5;

		private List<Thing> hauledThingsInInventory = new List<Thing>(50);

		public override void Notify_Starting() {
			base.Notify_Starting();

			if (!hauledThingsInInventory.NullOrEmpty()) {
				Log.Error($"Started {this.GetType()} job with non empty inventory...");
				BetterHaulUtils.dropAllInventoryHauledThings(pawn, hauledThingsInInventory);
			}
		}

		public override void ExposeData() {
			base.ExposeData();

			Scribe_Collections.Look<Thing>(ref this.hauledThingsInInventory, "hauledThingsInInventory", LookMode.Reference);
		}

		protected override IEnumerable<Toil> MakeNewToils() {
			base.AddEndCondition(delegate {
				Thing thing = base.GetActor().jobs.curJob.GetTarget(TargetIndex.A).Thing;
				if (thing is Building && !thing.Spawned) {
					return JobCondition.Incompletable;
				}
				return JobCondition.Ongoing;
			});
			this.AddEndCondition(delegate {
				foreach (var thing in hauledThingsInInventory) {
					if (!pawn.inventory.Contains(thing)) {
						return JobCondition.Incompletable;
					}
				}
				return JobCondition.Ongoing;
			});

			this.FailOnBurningImmobile(TargetIndex.A);
			this.FailOn(delegate () {
				IBillGiver billGiver = this.job.GetTarget(TargetIndex.A).Thing as IBillGiver;
				if (billGiver != null) {
					if (this.job.bill.DeletedOrDereferenced) {
						return true;
					}
					if (!billGiver.CurrentlyUsableForBills()) {
						return true;
					}
				}
				return false;
			});
			this.AddFinishAction(delegate () {
				BetterHaulUtils.dropAllInventoryHauledThings(pawn, hauledThingsInInventory);
			});


			Toil gotoBillGiver = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

			yield return new Toil {
				initAction = delegate () {
					if (this.job.targetQueueB != null && this.job.targetQueueB.Count == 1) {
						UnfinishedThing unfinishedThing = this.job.targetQueueB[0].Thing as UnfinishedThing;
						if (unfinishedThing != null) {
							unfinishedThing.BoundBill = (Bill_ProductionWithUft)this.job.bill;
						}
					}
				}
			};
			yield return Toils_Jump.JumpIf(gotoBillGiver, () => this.job.GetTargetQueue(TargetIndex.B).NullOrEmpty<LocalTargetInfo>());

			// Take from nearest to farthest
			Toil prepareExtract = defineSortQueueByDistance(TargetIndex.B);
			yield return prepareExtract;
			yield return Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B, true);

			Toil getToHaulTarget = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
			yield return getToHaulTarget;

			yield return BetterHaulUtils.definePickThingToil(TargetIndex.B, hauledThingsInInventory, true);

			// Re-sort before checking for next thing to haul.
			yield return defineSortQueueByDistance(TargetIndex.B);
			yield return defineJumpToCollectNextHaulableForBill(getToHaulTarget, TargetIndex.B);

			yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell).FailOnDestroyedOrNull(TargetIndex.B);

			Toil findPlaceTarget = Toils_JobTransforms.SetTargetToIngredientPlaceCell(TargetIndex.A, TargetIndex.B, TargetIndex.C);
			yield return findPlaceTarget;

			yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.C, findPlaceTarget, false);

			// check if need to drop other thing in inventory and repeat the find place.
			yield return defineJumpToDropNextHauledForBill(TargetIndex.B, findPlaceTarget, hauledThingsInInventory);

			yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.B, prepareExtract);

			yield return gotoBillGiver;
			yield return Toils_Recipe.MakeUnfinishedThingIfNeeded();
			yield return Toils_Recipe.DoRecipeWork().FailOnDespawnedNullOrForbiddenPlacedThings().FailOnCannotTouch(TargetIndex.A, PathEndMode.InteractionCell);
			yield return Toils_Recipe.FinishRecipeAndStartStoringProduct();
			if (!this.job.RecipeDef.products.NullOrEmpty<ThingDefCountClass>() || !this.job.RecipeDef.specialProducts.NullOrEmpty<SpecialProductType>()) {
				yield return Toils_Reserve.Reserve(TargetIndex.B, 1, -1, null);
				Toil carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
				yield return carryToCell;
				yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
				Toil recount = new Toil();
				recount.initAction = delegate () {
					Bill_Production bill_Production = recount.actor.jobs.curJob.bill as Bill_Production;
					if (bill_Production != null && bill_Production.repeatMode == BillRepeatModeDefOf.TargetCount) {
						this.Map.resourceCounter.UpdateResourceCounts();
					}
				};
				yield return recount;
			}
			yield break;
		}

		private static Toil defineSortQueueByDistance(TargetIndex ind) {
			Toil toil = new Toil();
			toil.initAction = delegate () {
				Pawn actor = toil.actor;
				Job curJob = actor.jobs.curJob;

				List<LocalTargetInfo> targetQueue = curJob.GetTargetQueue(ind);
				List<int> countQueue = curJob.countQueue;
				if (targetQueue.NullOrEmpty()) {
					return;
				}

				// Map count to targets.
				Dictionary<LocalTargetInfo, int> targetCountMap = new Dictionary<LocalTargetInfo, int>();
				for (int i = 0; i < targetQueue.Count; i++) {
					targetCountMap.Add(targetQueue[i], countQueue[i]);
				}

				// Sort by distance.
				// TODO: Calculate distance by path.
				targetQueue.SortBy(c => c.Thing.Position.DistanceToSquared(actor.Position));

				// Reorder counts accordly.
				for (int i = 0; i < targetQueue.Count; i++) {
					countQueue[i] = targetCountMap[targetQueue[i]];
				}
			};
			return toil;
		}

		private static Toil defineJumpToCollectNextHaulableForBill(Toil gotoGetTargetToil, TargetIndex ingredientInd) {
			Toil toil = new Toil();
			toil.initAction = delegate () {
				Pawn actor = toil.actor;
				Job curJob = actor.jobs.curJob;

				List<LocalTargetInfo> targetQueue = curJob.GetTargetQueue(ingredientInd);
				if (targetQueue.NullOrEmpty()) {
					return;
				}

				for (int i = 0; i < targetQueue.Count; i++) {
					var target = targetQueue[i];
					if (GenAI.CanUseItemForWork(actor, target.Thing)) {
						// Considering that everything must be hauled to the workplace, there is really no reason why something 
						// should be placed before taking the next one.
						int stackSize = Mathf.Min(curJob.countQueue[i], BetterHaulUtils.getAllHaulableSize(target.Thing, actor));
						if (stackSize > 0) {
							curJob.count = stackSize;
							curJob.SetTarget(ingredientInd, target);

							curJob.countQueue[i] -= stackSize;
							if (curJob.countQueue[i] <= 0) {
								curJob.countQueue.RemoveAt(i);
								targetQueue.RemoveAt(i);
							}
							actor.jobs.curDriver.JumpToToil(gotoGetTargetToil);
							return;
						}
					}
				}
			};
			return toil;
		}

		private static Toil defineJumpToDropNextHauledForBill(TargetIndex ind, Toil findPlaceTarget, List<Thing> hauledThingsInInventory) {
			// Changed to check inventory other then carry capacity.

			Toil toil = new Toil();
			toil.initAction = delegate () {
				Pawn actor = toil.actor;
				Job curJob = actor.jobs.curJob;

				if (hauledThingsInInventory.NullOrEmpty()) {
					return;
				}

				Thing thing = hauledThingsInInventory.First();

				int initialStack = thing.stackCount;
				int availableSpace = actor.carryTracker.AvailableStackSpace(thing.def);

				Thing resThing;
				int movedQuantity =
					actor.inventory.innerContainer.TryTransferToContainer(thing, actor.carryTracker.innerContainer, availableSpace, out resThing, true);
				if (initialStack == movedQuantity) {
					// Remove thing if everything has been moved from the inventory.
					hauledThingsInInventory.Remove(thing);
				}

				if (resThing == null || movedQuantity == 0) {
					throw new Exception("Error moving hauled thing to carry container = " + thing);
				}

				// Set the moved thing as current target.
				curJob.SetTarget(ind, resThing);

				actor.jobs.curDriver.JumpToToil(findPlaceTarget);
			};
			return toil;
		}
	}
}

