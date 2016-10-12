#region Copyright & License Information
/*
 * Copyright 2016 OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using OpenRA.Activities;
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Activities
{
	public class DirectlyHarvestResource : Activity
	{
		readonly RefineryHarvester harv;
		readonly RefineryHarvesterInfo harvInfo;
		readonly IFacing facing;
		readonly ResourceClaimLayer territory;
		readonly ResourceLayer resLayer;
		readonly BodyOrientation body;

		public DirectlyHarvestResource(Actor self)
		{
			harv = self.Trait<RefineryHarvester>();
			harvInfo = harv.Info;
			facing = self.Trait<IFacing>();
			body = self.Trait<BodyOrientation>();
			territory = self.World.WorldActor.TraitOrDefault<ResourceClaimLayer>();
			resLayer = self.World.WorldActor.Trait<ResourceLayer>();
		}

		public override Activity Tick(Actor self)
		{
			if (IsCanceled || harv.IsFull)
			{
				if (territory != null)
					territory.UnclaimByActor(self);

				return NextActivity;
			}

			harv.LastHarvestedCell = self.Location;

			// Turn to one of the harvestable facings
			if (harvInfo.HarvestFacings != 0)
			{
				var current = facing.Facing;
				var desired = body.QuantizeFacing(current, harvInfo.HarvestFacings);
				if (desired != current)
					return ActivityUtils.SequenceActivities(new Turn(self, desired), this);
			}

			var resource = resLayer.Harvest(self.Location);
			if (resource == null)
			{
				if (territory != null)
					territory.UnclaimByActor(self);

				return NextActivity;
			}

			harv.AcceptResource(resource);

			foreach (var t in self.TraitsImplementing<INotifyHarvesterAction>())
				t.Harvested(self, resource);

			return ActivityUtils.SequenceActivities(new Wait(harvInfo.BaleLoadDelay), this);
		}
	}
}
