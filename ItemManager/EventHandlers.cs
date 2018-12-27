﻿using Smod2.API;
using Smod2.EventHandlers;
using Smod2.Events;

using scp4aiur;
using ItemManager.Recipes;
using ItemManager.Events;

using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

using System;
using System.Linq;
using System.Collections.Generic;

namespace ItemManager
{
    public class EventHandlers : IEventHandlerRoundStart, IEventHandlerRoundRestart, IEventHandlerPlayerPickupItemLate, 
        IEventHandlerPlayerDropItem, IEventHandlerSCP914Activate, IEventHandlerPlayerHurt, IEventHandlerShoot, 
        IEventHandlerMedkitUse, IEventHandlerPlayerDie, IEventHandlerRadioSwitch
    {
        public void OnRoundStart(RoundStartEvent ev)
        {
            Plugin.heldItems = Plugin.instance.GetConfigInt("itemmanager_helditems");
            Plugin.giveRanks = Plugin.instance.GetConfigList("itemmanager_give_ranks");

            Items.scp = Object.FindObjectOfType<Scp914>();
            Items.hostInventory = GameObject.Find("Host").GetComponent<Inventory>();
        }

        public void OnRoundRestart(RoundRestartEvent ev)
        {
            foreach (float uniq in Items.customItems.Keys.ToArray())
            {
                Items.customItems[uniq].Unhook();
                Items.customItems.Remove(uniq);
            }
        }

        private static void InvokePickupEvent(CustomItem customItem, GameObject player, Inventory inventory, int index, Inventory.SyncItemInfo item)
        {
            customItem.PlayerObject = player;
            customItem.Inventory = inventory;
            customItem.Index = index;
            customItem.Dropped = null;

            customItem.ApplyInventory();

            if (!customItem.OnPickup())
            {
                inventory.items.RemoveAt(index);

                customItem.PlayerObject = null;
                customItem.Inventory = null;
                customItem.Index = -1;
                customItem.Dropped = Items.hostInventory.SetPickup(item.id, customItem.UniqueId, player.transform.position,
                    player.transform.rotation, item.modSight, item.modBarrel, item.modOther).GetComponent<Pickup>();

                customItem.ApplyPickup();
            }
        }

        private static void BaseInvokeDropEvent(CustomItem customItem, Inventory inventory, int index,
            Pickup drop, Func<bool> result)
        {
            customItem.Dropped = drop;

            customItem.ApplyPickup();

            if (!result())
            {
                Items.ReinsertItem(inventory, index, drop.info);
                customItem.Dropped = null;
                drop.Delete();

                customItem.ApplyInventory();
            }
            else
            {
                customItem.PlayerObject = null;
                customItem.Inventory = null;
                customItem.Index = -1;
            }
        }

        private static void InvokeDropEvent(CustomItem customItem, Inventory inventory, int index, Pickup drop)
        {
            BaseInvokeDropEvent(customItem, inventory, index, drop, customItem.OnDrop);
        }

        private static void InvokeDoubleDropEvent(CustomItem customItem, Inventory inventory, int index, Pickup drop)
        {
            BaseInvokeDropEvent(customItem, inventory, index, drop, ((IDoubleDroppable)customItem).OnDoubleDrop);
        }

        private static void InvokeDeathDropEvent(CustomItem customItem, Pickup drop, GameObject attacker, DamageType damage)
        {
            customItem.Dropped = drop;

            customItem.ApplyPickup();

            if (!customItem.OnDeathDrop(attacker, damage))
            {
                customItem.Dropped = null;

                drop.Delete();
                customItem.Unhook();
            }
            else
            {
                customItem.PlayerObject = null;
                customItem.Inventory = null;
                customItem.Index = -1;
            }
        }

        public void OnPlayerPickupItemLate(PlayerPickupItemLateEvent ev)
        {
            GameObject player = (GameObject)ev.Player.GetGameObject();
            Inventory inventory = player.GetComponent<Inventory>();

            Timing.Next(() =>
            {
                Inventory.SyncItemInfo? item = null;
                try
                {
                    item = inventory.items.Last();
                }
                catch { }

                if (item != null && Items.customItems.ContainsKey(item.Value.durability))
                {
                    CustomItem customItem = Items.customItems[item.Value.durability];

                    InvokePickupEvent(customItem, player, inventory, inventory.items.Count - 1, item.Value);
                }
            });
        }

        public void OnPlayerDropItem(PlayerDropItemEvent ev)
        {
            GameObject player = (GameObject)ev.Player.GetGameObject();
            Inventory inventory = player.GetComponent<Inventory>();

            GetLostItem(inventory, out Pickup[] prePickups, out int[] preItems);

            Timing.Next(() => {
                Pickup drop = GetLostItemTick(inventory, prePickups, preItems, out int dropIndex);

                if (dropIndex == -1)
                {
                    return;
                }

                CustomItem customItem = Items.FindCustomItem(player, dropIndex);
                Items.CorrectItemIndexes(Items.GetCustomItems(inventory.gameObject), dropIndex);

                switch (customItem) {
                    case null:
                        return;

                    case IDoubleDroppable doubleDroppable when doubleDroppable.DoubleDropWindow > 0:
                    {
                        if (Items.readyForDoubleDrop[customItem.UniqueId])
                        {
                            Items.readyForDoubleDrop[customItem.UniqueId] = false;
                            Timing.Remove(Items.doubleDropTimers[customItem.UniqueId]);

                            InvokeDoubleDropEvent(customItem, inventory, dropIndex, drop);
                        }
                        else
                        {
                            Pickup.PickupInfo info = drop.info;
                            drop.Delete(); //delete dropped item
                            Inventory.SyncItemInfo doubleDropDummy = Items.ReinsertItem(inventory, dropIndex, info); //add item back to inventory
                            Items.readyForDoubleDrop[customItem.UniqueId] = true;

                            Items.doubleDropTimers.Remove(customItem.UniqueId);
                            Items.doubleDropTimers.Add(customItem.UniqueId, Timing.In(inaccuracy => {
                                Items.readyForDoubleDrop[customItem.UniqueId] = false;
                                inventory.items.Remove(doubleDropDummy); //remove dummy from inventory
                                drop = Items.hostInventory //create item in world
                                    .SetPickup(info.itemId, info.durability, player.transform.position, player.transform.rotation, info.weaponMods[0], info.weaponMods[1], info.weaponMods[2])
                                    .GetComponent<Pickup>();

                                InvokeDropEvent(customItem, inventory, dropIndex, drop);
                            }, doubleDroppable.DoubleDropWindow));
                        }

                        break;
                    }

                    default:
                        InvokeDropEvent(customItem, inventory, dropIndex, drop);
                        break;
                }
            });
        }

        public void OnSCP914Activate(SCP914ActivateEvent ev)
        {
            Collider[] colliders = ev.Inputs.Cast<Collider>().ToArray();

            foreach (Pickup pickup in colliders.Select(x => x.GetComponent<Pickup>()).Where(x => x != null))
            {
                if (Items.customItems.ContainsKey(pickup.info.durability))
                {
                    CustomItem item = Items.customItems[pickup.info.durability];

                    Base914Recipe recipe = Items.recipes.Where(x => x.IsMatch(ev.KnobSetting, item, false))
                        .OrderByDescending(x => x.Priority).FirstOrDefault(); //gets highest priority

                    item.Dropped = Items.hostInventory.SetPickup((int)item.Type, pickup.info.durability,
                        pickup.info.position + (Items.scp.output_obj.position - Items.scp.intake_obj.position),
                        pickup.info.rotation, item.Sight, item.Barrel, item.MiscAttachment).GetComponent<Pickup>();
                    pickup.Delete();

                    if (recipe != null)
                    { //recipe has higher priority
                        recipe.Run(item, false);
                    }
                    else
                    {
                        item.On914(ev.KnobSetting, item.Dropped.transform.position, false);
                    }
                }
                else
                {
                    Pickup.PickupInfo info = pickup.info;

                    Base914Recipe recipe = Items.recipes.Where(x => x.IsMatch(ev.KnobSetting, info))
                        .OrderByDescending(x => x.Priority).FirstOrDefault();

                    if (recipe != null)
                    {
                        pickup.Delete();

                        recipe.Run(Items.hostInventory.SetPickup(info.itemId,
                            info.durability,
                            info.position + (Items.scp.output_obj.position - Items.scp.intake_obj.position),
                            info.rotation, info.weaponMods[0], info.weaponMods[1], info.weaponMods[2]).GetComponent<Pickup>());
                    }
                }
            }

            if (Plugin.heldItems > 0)
            {
                foreach (Inventory inventory in colliders.Select(x => x.GetComponent<Inventory>()).Where(x => x != null))
                {
                    for (int i = 0; i < inventory.items.Count; i++)
                    {
                        CustomItem item = Items.FindCustomItem(inventory.gameObject, i);

                        if (item == null)
                        {
                            if (Plugin.heldItems == 1 || Plugin.heldItems == 3)
                            {
                                Base914Recipe recipe = Items.recipes.Where(x => x.IsMatch(ev.KnobSetting, inventory, i))
                                    .OrderByDescending(x => x.Priority).FirstOrDefault();

                                if (recipe != null)
                                {
                                    recipe.Run(inventory, i);
                                }
                                else
                                {
                                    byte itemId = (byte)inventory.items[i].id;
                                    byte knobId = (byte)ev.KnobSetting;
                                    sbyte outputType = (sbyte)Items.scp.recipes[itemId].outputs[knobId].outputs[Random.Range(0, Items.scp.recipes[itemId].outputs[knobId].outputs.Count)];

                                    if (outputType > 0)
                                    {
                                        inventory.items[i] = new Inventory.SyncItemInfo
                                        {
                                            id = outputType,
                                            uniq = inventory.items[i].uniq
                                        };
                                    }
                                    else
                                    {
                                        Items.CorrectItemIndexes(Items.GetCustomItems(inventory.gameObject), i);
                                        inventory.items.RemoveAt(i);
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (Plugin.heldItems == 2 || Plugin.heldItems == 3)
                            {
                                Base914Recipe recipe = Items.recipes.Where(x => x.IsMatch(ev.KnobSetting, item, true))
                                    .OrderByDescending(x => x.Priority).FirstOrDefault(); //gets highest priority

                                if (recipe != null)
                                {
                                    recipe.Run(item, true);
                                }
                                else
                                {
                                    item.On914(ev.KnobSetting, item.PlayerObject.transform.position + (Items.scp.output_obj.position - Items.scp.intake_obj.position), true);
                                }
                            }
                        }
                    }
                }
            }
        }

        public void OnPlayerHurt(PlayerHurtEvent ev)
        {
            CustomItem customItem = ev.Attacker?.HeldCustomItem();

            if (customItem != null && customItem is IWeapon weapon)
            {
                WeaponManager manager = customItem.PlayerObject.GetComponent<WeaponManager>();
                int index = manager.weapons.TakeWhile(x => x.inventoryID != (int)customItem.Type).Count();
                if (index == manager.weapons.Length)
                {
                    return;
                }

                if (weapon.CurrentAmmo <= 0 && customItem.Durability == manager.weapons[index].maxAmmo)
                {
                    weapon.CurrentAmmo = weapon.MagazineSize;
                }

                if (weapon.CurrentAmmo > 0)
                {
                    float damage = ev.Damage;
                    weapon.OnShoot((GameObject)ev.Player.GetGameObject(), ref damage);
                    ev.Damage = damage;

                    AdjustWeapon(customItem, weapon, manager, index);
                }
                else
                {
                    ev.Damage = 0; //player shouldnt have shot >:(
                }
            }
        }

        public void OnShoot(PlayerShootEvent ev)
        {
            if (ev.Target == null && ev.Player != null)
            {
                CustomItem customItem = ev.Player.HeldCustomItem();

                if (customItem != null && customItem is IWeapon weapon && weapon.CurrentAmmo > 0)
                {
                    WeaponManager manager = customItem.PlayerObject.GetComponent<WeaponManager>();
                    int index = manager.weapons.TakeWhile(x => x.inventoryID != (int)customItem.Type).Count();
                    if (index == manager.weapons.Length)
                    {
                        return;
                    }

                    float damage = 0;
                    weapon.OnShoot(null, ref damage);

                    AdjustWeapon(customItem, weapon, manager, index);
                }
            }
        }

        private static void AdjustWeapon(CustomItem item, IWeapon weapon, WeaponManager manager, int index)
        {
            if (--weapon.CurrentAmmo <= 0)
            {
                item.Durability = 0;

                // Gives the player their entire magazine back so it doesnt actually use any ammo
                AmmoBox ammo = item.PlayerObject.GetComponent<AmmoBox>();
                ammo.SetOneAmount(manager.weapons[index].ammoType, (ammo.GetAmmo(manager.weapons[index].ammoType) + manager.weapons[index].maxAmmo).ToString());
            }
            else if (weapon.FireRate > 0)
            {
                item.Durability = 0;

                Timing.In(x =>
                {
                    item.Durability = manager.weapons[index].maxAmmo;
                }, weapon.FireRate);
            }
        }

        public void OnMedkitUse(PlayerMedkitUseEvent ev)
        {
            GameObject player = (GameObject)ev.Player.GetGameObject();
            Inventory inventory = player.GetComponent<Inventory>();
            GetLostItem(inventory, out Inventory.SyncItemInfo[] preItems);

            Timing.Next(() => {
                GetLostItemTick(inventory, preItems, out int index);

                if (index == -1)
                {
                    return;
                }

                CustomItem item = Items.FindCustomItem(player, index);
                Items.CorrectItemIndexes(ev.Player.GetCustomItems(), index);

                item?.OnMedkitUse();
            });
        }

        private static void GetLostItem(Inventory inventory, out Inventory.SyncItemInfo[] preItems)
        {
            preItems = inventory.items.ToArray();
        }

        private static Inventory.SyncItemInfo GetLostItemTick(Inventory inventory, Inventory.SyncItemInfo[] preItems, out int index)
        {
            if (preItems.Length == inventory.items.Count)
            {
                index = -1;
                return default(Inventory.SyncItemInfo);
            }

            int[] postItems = inventory.items.Select(x => x.uniq).ToArray();
            
            index = postItems.Length;
            for (int i = 0; i < postItems.Length; i++)
            {
                if (preItems[i].uniq != postItems[i])
                {
                    index = i;

                    break;
                }
            }

            return preItems[index];
        }

        private static void GetLostItem(Inventory inventory, out Pickup[] prePickups, out int[] preItems)
        {
            prePickups = Object.FindObjectsOfType<Pickup>();
            preItems = inventory.items.Select(x => x.uniq).ToArray();
        }

        private static Pickup GetLostItemTick(Inventory inventory, Pickup[] prePickups, int[] preItems, out int index)
        {
            Pickup[] postPickups = Object.FindObjectsOfType<Pickup>();

            if (postPickups.Length == prePickups.Length)
            {
                index = -1;
                return null;
            }

            int[] postItems = inventory.items.Select(x => x.uniq).ToArray();

            index = postItems.Length;
            for (int i = 0; i < postItems.Length; i++)
            {
                if (preItems[i] != postItems[i])
                {
                    index = i;

                    break;
                }
            }

            return postPickups.Except(prePickups).First();
        }

        public void OnPlayerDie(PlayerDeathEvent ev)
        {
            List<CustomItem> items = ev.Player.GetCustomItems().ToList();

            if (items.Count > 0)
            {
                Dictionary<CustomItem, ItemType> itemTypes = items.ToDictionary(x => x, x => x.Type);
                Vector3 deathPosition = ((GameObject)ev.Player.GetGameObject()).transform.position;
                Pickup[] prePickups = Object.FindObjectsOfType<Pickup>();

                Timing.Next(() => {
                    Pickup[] postPickups = Object.FindObjectsOfType<Pickup>();
                    Pickup[] pickupsThisTick = postPickups.Except(prePickups).ToArray();
                    Pickup[] deathPickups = pickupsThisTick
                        .Where(x => Vector3.Distance(deathPosition, x.transform.position) < 10).ToArray();

                    foreach (Pickup pickup in deathPickups)
                    {
                        CustomItem customItemOfType = itemTypes.FirstOrDefault(x => (int)x.Value == pickup.info.itemId).Key;

                        if (customItemOfType != null)
                        {
                            items.Remove(customItemOfType);

                            InvokeDeathDropEvent(customItemOfType, pickup, (GameObject)ev.Killer.GetGameObject(), ev.DamageTypeVar);
                        }
                    }
                });
            }
        }

        public void OnPlayerRadioSwitch(PlayerRadioSwitchEvent ev)
        {
            foreach (CustomItem customItem in ev.Player.GetCustomItems())
            {
                customItem.OnRadioSwitch(ev.ChangeTo);
            }
        }
    }
}