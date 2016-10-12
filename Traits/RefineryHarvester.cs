#region Copyright & License Information
/*
 * Copyright 2016 OpenRA.Mods.AS Developers (see AUTHORS)
 * This file is a part of a third-party plugin for OpenRA, which is
 * free software. It is made available to you under the terms of the
 * GNU General Public License as published by the Free Software
 * Foundation. For more information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.AS.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.AS.Traits
{
	public class RefineryHarvesterInfo : ITraitInfo, Requires<MobileInfo>, Requires<WithSpriteBodyInfo>
	{
		[Desc("How much resources it can carry.")]
		public readonly int Capacity = 28;

		public readonly int BaleLoadDelay = 4; // ?

		[Desc("How many squares to show the fill level.")]
		public readonly int PipCount = 7;

		public readonly int HarvestFacings = 0;

		[Desc("Which resources it can harvest.")]
		public readonly HashSet<string> Resources = new HashSet<string>();

		[Desc("Percentage of maximum speed when fully loaded.")]
		public readonly int FullyLoadedSpeed = 85;

		[Desc("Automatically scan for resources when created.")]
		public readonly bool SearchOnCreation = true;

		[Desc("Initial search radius (in cells) from the refinery that created us.")]
		public readonly int SearchFromProcRadius = 24; // from this actor, not ref

		[Desc("Search radius (in cells) from the last harvest order location to find more resources.")]
		public readonly int SearchFromOrderRadius = 12;

		[Desc("Maximum duration of being idle before queueing a Wait activity.")]
		public readonly int MaxIdleDuration = 25;

		[Desc("Duration to wait before becoming idle again.")]
		public readonly int WaitDuration = 25;

		[VoiceReference] public readonly string HarvestVoice = "Action";

		[Desc("Discard resources once silo capacity has been reached.")]
		public readonly bool DiscardExcessResources = false;

		public readonly bool ShowTicks = true;
		public readonly int TickLifetime = 30;
		public readonly int TickVelocity = 2;
		public readonly int TickRate = 10;

		[Desc("How long it takes to make collected ore to cash.")] // TODO
		public readonly int RefinementDuration = 0;

		public virtual object Create(ActorInitializer init) { return new RefineryHarvester(init.Self, this); }
	}

	public class RefineryHarvester : IIssueOrder, IResolveOrder, IPips,
		IExplodeModifier, IOrderVoice, ISpeedModifier, ISync, INotifyCreated,
		INotifyResourceClaimLost, INotifyIdle, INotifyBlockingMove, INotifyBuildComplete,
		ITick, INotifyOwnerChanged
	{
		readonly Actor self;
		public readonly RefineryHarvesterInfo Info;
		readonly Mobile mobile;
		readonly Dictionary<ResourceTypeInfo, int> contents = new Dictionary<ResourceTypeInfo, int>();
		readonly WithSpriteBody wsb;

		PlayerResources playerResources;
		bool idleSmart = true;
		int currentDisplayTick = 0;
		int currentDisplayValue = 0;
		int currentUnloadTicks = 0; // TODO: Rename

		public CPos? LastHarvestedCell = null;
		public CPos? LastOrderLocation = null;
		[Sync] public int Ore = 0;
		[Sync] public int ContentValue
		{
			get
			{
				var value = 0;
				foreach (var c in contents)
					value += c.Key.ValuePerUnit * c.Value;

				return value;
			}
		}

		public RefineryHarvester(Actor self, RefineryHarvesterInfo info)
		{
			this.self = self;
			Info = info;
			mobile = self.Trait<Mobile>();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			currentDisplayTick = info.TickRate;
			wsb = self.Trait<WithSpriteBody>();
		}

		public bool CanGiveResource(int amount)
		{
			return Info.DiscardExcessResources || playerResources.CanGiveResources(amount);
		}

		public void GiveResource(int amount)
		{
			if (Info.DiscardExcessResources)
				amount = Math.Min(amount, playerResources.ResourceCapacity - playerResources.Resources);

			playerResources.GiveResources(amount);
			if (Info.ShowTicks)
				currentDisplayValue += amount;
		}

		public bool RefineResources()
		{
			// Wait until the next bale is ready
			if (--currentUnloadTicks > 0)
				return true;

			if (contents.Keys.Count > 0)
			{
				var type = contents.First().Key;
				if (!CanGiveResource(type.ValuePerUnit))
					return false;

				// WTF? why the duplication; copy and pasted from TickUnload
				GiveResource(type.ValuePerUnit);
				if (--contents[type] == 0)
					contents.Remove(type);

				currentUnloadTicks = Info.RefinementDuration;
			}

			return contents.Count == 0;
		}

		public void Tick(Actor self)
		{
			if (Info.ShowTicks && currentDisplayValue > 0 && --currentDisplayTick <= 0)
			{
				var temp = currentDisplayValue;
				if (self.Owner.IsAlliedWith(self.World.RenderPlayer))
					self.World.AddFrameEndTask(w => w.Add(new FloatingText(self.CenterPosition, self.Owner.Color.RGB, FloatingText.FormatCashTick(temp), 30)));

				currentDisplayTick = Info.TickRate;
				currentDisplayValue = 0;
			}
		}

		public void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			playerResources = newOwner.PlayerActor.Trait<PlayerResources>();
		}

		public bool ShouldExplode(Actor self) { return Ore > 0; }

		public void Created(Actor self)
		{
			if (Info.SearchOnCreation)
				self.QueueActivity(new DirectlyCollectResources(self, this));
		}

		public void BuildingComplete(Actor self)
		{
			if (Info.SearchOnCreation)
				self.QueueActivity(new DirectlyCollectResources(self, this));
		}

		public void ContinueHarvesting(Actor self)
		{
			// Move out of the refinery dock and continue harvesting
			// UnblockRefinery(self);
			self.QueueActivity(new DirectlyCollectResources(self, this));
		}

		public bool IsFull { get { return contents.Values.Sum() == Info.Capacity; } }
		public bool IsEmpty { get { return contents.Values.Sum() == 0; } }
		public int Fullness { get { return contents.Values.Sum() * 100 / Info.Capacity; } }

		public void AcceptResource(ResourceType type)
		{
			if (!contents.ContainsKey(type.Info))
				contents[type.Info] = 1;
			else
				contents[type.Info]++;
		}

		// ?
		public void OnNotifyBlockingMove(Actor self, Actor blocking)
		{
			// I'm blocking someone else from moving to my location:
			var act = self.GetCurrentActivity();

			// If I'm just waiting around then get out of the way:
			if (act is Wait)
			{
				self.CancelActivity();

				var cell = self.Location;
				var moveTo = mobile.NearestMoveableCell(cell, 2, 5);
				self.QueueActivity(mobile.MoveTo(moveTo, 0));
				self.SetTargetLine(Target.FromCell(self.World, moveTo), Color.Gray, false);

				// Find more resources but not at this location:
				self.QueueActivity(new DirectlyCollectResources(self, this, cell));
			}
		}

		int idleDuration;
		public void TickIdle(Actor self)
		{
			// Should we be intelligent while idle?
			if (!idleSmart)
				return;

			// Are we not empty? Deliver resources:
			if (!IsEmpty)
			{
				//self.QueueActivity(new DeliverResources(self));
				self.QueueActivity(new DirectlyCollectResources(self, this));
				return;
			}

			// UnblockRefinery(self);
			idleDuration += 1;

			// Wait a bit before queueing Wait activity
			if (idleDuration > Info.MaxIdleDuration)
			{
				idleDuration = 0;

				// Wait for a bit before becoming idle again:
				self.QueueActivity(new Wait(Info.WaitDuration));
			}
		}

		public IEnumerable<IOrderTargeter> Orders
		{
			get
			{
				yield return new RefineryHarvestOrderTargeter();
			}
		}

		public Order IssueOrder(Actor self, IOrderTargeter order, Target target, bool queued)
		{
			if (order.OrderID == "Harvest")
				return new Order(order.OrderID, self, queued) { TargetLocation = self.World.Map.CellContaining(target.CenterPosition) };

			return null;
		}

		public string VoicePhraseForOrder(Actor self, Order order)
		{
			if (order.OrderString == "Harvest")
				return Info.HarvestVoice;

			return null;
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "Harvest")
			{
				idleSmart = true;

				self.CancelActivity();

				CPos? loc;
				if (order.TargetLocation != CPos.Zero)
				{
					loc = order.TargetLocation;

					var territory = self.World.WorldActor.TraitOrDefault<ResourceClaimLayer>();
					if (territory != null)
					{
						// Find the nearest claimable cell to the order location (useful for group-select harvest):
						loc = mobile.NearestCell(loc.Value, p => mobile.CanEnterCell(p) && territory.ClaimResource(self, p), 1, 6);
					}
					else
					{
						// Find the nearest cell to the order location (useful for group-select harvest):
						var taken = new HashSet<CPos>();
						loc = mobile.NearestCell(loc.Value, p => mobile.CanEnterCell(p) && taken.Add(p), 1, 6);
					}
				}
				else
				{
					// A bot order gives us a CPos.Zero TargetLocation.
					loc = self.Location;
				}

				var collectResources = new DirectlyCollectResources(self, this);
				self.QueueActivity(collectResources);
				self.SetTargetLine(Target.FromCell(self.World, loc.Value), Color.Red);

				var notify = self.TraitsImplementing<INotifyHarvesterAction>();
				foreach (var n in notify)
					n.MovingToResources(self, loc.Value, collectResources);

				LastOrderLocation = loc;

				// This prevents harvesters returning to an empty patch when the player orders them to a new patch:
				LastHarvestedCell = LastOrderLocation;
			}
			else if (order.OrderString == "Stop" || order.OrderString == "Move")
			{
				var notify = self.TraitsImplementing<INotifyHarvesterAction>();
				foreach (var n in notify)
					n.MovementCancelled(self);

				// Turn off idle smarts to obey the stop/move:
				idleSmart = false;
			}
		}

		// Copy paste from Harvester.cs below

		public void OnNotifyResourceClaimLost(Actor self, ResourceClaim claim, Actor claimer)
		{
			if (self == claimer) return;

			// Our claim on a resource was stolen, find more unclaimed resources:
			self.CancelActivity();
			self.QueueActivity(new DirectlyCollectResources(self, this));
		}

		PipType GetPipAt(int i)
		{
			var n = i * Info.Capacity / Info.PipCount;

			foreach (var rt in contents)
				if (n < rt.Value)
					return rt.Key.PipColor;
				else
					n -= rt.Value;

			return PipType.Transparent;
		}

		public IEnumerable<PipType> GetPips(Actor self)
		{
			var numPips = Info.PipCount;

			for (var i = 0; i < numPips; i++)
				yield return GetPipAt(i);
		}

		public int GetSpeedModifier()
		{
			return 100 - (100 - Info.FullyLoadedSpeed) * contents.Values.Sum() / Info.Capacity;
		}

		class RefineryHarvestOrderTargeter : IOrderTargeter
		{
			public string OrderID { get { return "Harvest"; } }
			public int OrderPriority { get { return 10; } }
			public bool IsQueued { get; protected set; }
			public bool TargetOverridesSelection(TargetModifiers modifiers) { return true; }

			public bool CanTarget(Actor self, Target target, List<Actor> othersAtTarget, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Terrain)
					return false;

				if (modifiers.HasModifier(TargetModifiers.ForceMove))
					return false;

				var location = self.World.Map.CellContaining(target.CenterPosition);

				// Don't leak info about resources under the shroud
				if (!self.Owner.Shroud.IsExplored(location))
					return false;

				var res = self.World.WorldActor.Trait<ResourceLayer>().GetRenderedResource(location);
				var info = self.Info.TraitInfo<RefineryHarvesterInfo>();

				if (res == null || !info.Resources.Contains(res.Info.Name))
					return false;

				cursor = "harvest";
				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);

				return true;
			}
		}
	}
}
