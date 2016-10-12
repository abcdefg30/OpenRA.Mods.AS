#region Copyright & License Information
/*
 * Copyright 2016 OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Drawing;
using OpenRA.Activities;
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class DirectlyCollectResources : Activity
	{
		readonly RefineryHarvester harv;
		readonly RefineryHarvesterInfo harvInfo;
		readonly Mobile mobile;
		readonly MobileInfo mobileInfo;
		readonly ResourceLayer resLayer;
		readonly ResourceClaimLayer territory;
		readonly IPathFinder pathFinder;
		readonly DomainIndex domainIndex;

		CPos? avoidCell;
		bool refiningResources;

		public DirectlyCollectResources(Actor self, RefineryHarvester harv)
		{
			this.harv = harv;
			harvInfo = harv.Info;
			mobile = self.Trait<Mobile>();
			mobileInfo = self.Info.TraitInfo<MobileInfo>();

			var worldActor = self.World.WorldActor;
			resLayer = worldActor.Trait<ResourceLayer>();
			territory = worldActor.TraitOrDefault<ResourceClaimLayer>();
			pathFinder = worldActor.Trait<IPathFinder>();
			domainIndex = worldActor.Trait<DomainIndex>();
		}

		public DirectlyCollectResources(Actor self, RefineryHarvester harv, CPos avoidCell)
			: this(self, harv)
		{
			this.avoidCell = avoidCell;
		}

		public override Activity Tick(Actor self)
		{
			if (IsCanceled || NextActivity != null)
				return NextActivity;

			if (refiningResources)
			{
				refiningResources = harv.RefineResources();
				return this;
			}

			// TODO:
			if (harv.IsFull)
			{
				refiningResources = true;
				return this;
			}

			var closestHarvestablePosition = ClosestHarvestablePos(self);

			// If no harvestable position could be found, either deliver the remaining resources
			// or get out of the way and do not disturb.
			if (!closestHarvestablePosition.HasValue)
			{
				if (!harv.IsEmpty)
				{
					refiningResources = true;
					return this;
				}

				var unblockCell = harv.LastHarvestedCell ?? self.Location;
				var moveTo = mobile.NearestMoveableCell(unblockCell, 2, 5);
				self.QueueActivity(mobile.MoveTo(moveTo, 1));
				self.SetTargetLine(Target.FromCell(self.World, moveTo), Color.Gray, false);

				// TODO: The harvest-deliver-return sequence is a horrible mess of duplicated code and edge-cases
				var notify = self.TraitsImplementing<INotifyHarvesterAction>();
				foreach (var n in notify)
					n.MovingToResources(self, moveTo, this);

				var randFrames = self.World.SharedRandom.Next(100, 175);

				// Avoid creating an activity cycle
				var next = NextActivity;
				NextActivity = null;
				return ActivityUtils.SequenceActivities(next, new Wait(randFrames), this);
			}
			else
			{
				// Attempt to claim a resource as ours
				if (territory != null && !territory.ClaimResource(self, closestHarvestablePosition.Value))
					return ActivityUtils.SequenceActivities(new Wait(25), this);

				// If not given a direct order, assume ordered to the first resource location we find:
				if (!harv.LastOrderLocation.HasValue)
					harv.LastOrderLocation = closestHarvestablePosition;

				self.SetTargetLine(Target.FromCell(self.World, closestHarvestablePosition.Value), Color.Red, false);

				// TODO: The harvest-deliver-return sequence is a horrible mess of duplicated code and edge-cases
				var notify = self.TraitsImplementing<INotifyHarvesterAction>();
				foreach (var n in notify)
					n.MovingToResources(self, closestHarvestablePosition.Value, this);

				return ActivityUtils.SequenceActivities(mobile.MoveTo(closestHarvestablePosition.Value, 1), new DirectlyHarvestResource(self), this);
			}
		}

		/// <summary>
		/// Finds the closest harvestable pos between the current position of the harvester
		/// and the last order location
		/// </summary>
		CPos? ClosestHarvestablePos(Actor self)
		{
			if (CanHarvestAt(self, self.Location))
				return self.Location;

			// Determine where to search from and how far to search:
			var searchFromLoc = harv.LastOrderLocation ?? self.Location;
			var searchRadius = harv.LastOrderLocation.HasValue ? harvInfo.SearchFromOrderRadius : harvInfo.SearchFromProcRadius;
			var searchRadiusSquared = searchRadius * searchRadius;

			// Find any harvestable resources:
			var passable = (uint)mobileInfo.GetMovementClass(self.World.Map.Rules.TileSet);
			List<CPos> path;
			using (var search = PathSearch.Search(self.World, mobileInfo, self, true,
				loc => domainIndex.IsPassable(self.Location, loc, passable) && CanHarvestAt(self, loc))
				.WithCustomCost(loc =>
				{
					if ((avoidCell.HasValue && loc == avoidCell.Value) ||
						(loc - self.Location).LengthSquared > searchRadiusSquared)
						return int.MaxValue;

					return 0;
				})
				.FromPoint(self.Location)
				.FromPoint(searchFromLoc))
				path = pathFinder.FindPath(search);

			if (path.Count > 0)
				return path[0];

			return null;
		}

		bool CanHarvestAt(Actor self, CPos pos)
		{
			var resType = resLayer.GetResource(pos);
			if (resType == null)
				return false;

			// Can the harvester collect this kind of resource?
			if (!harvInfo.Resources.Contains(resType.Info.Name))
				return false;

			if (territory != null)
			{
				// Another harvester has claimed this resource:
				ResourceClaim claim;
				if (territory.IsClaimedByAnyoneElse(self as Actor, pos, out claim))
					return false;
			}

			return true;
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			yield return Target.FromCell(self.World, self.Location);
		}
	}
}
