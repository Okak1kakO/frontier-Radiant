using Content.Shared.Containers.ItemSlots;
using Content.Shared._radiant.WeaponSerial.Components;

namespace Content.Shared._radiant.WeaponSerial;


/// <summary>
///     Shared part of the weapon serial system. Registers the console slot
///     so that the weapon can be inserted by clicking it in.
/// </summary>
public abstract partial class SharedWeaponSerialSystem : EntitySystem
{
    /// <summary>Slot ID used inside ItemSlotsSystem.</summary>
    public const string WeaponSlotId = "WeaponRegistrationConsole-weaponSlot";

    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WeaponRegistrationConsoleComponent, ComponentInit>(OnComponentInit);
    }

    private void OnComponentInit(EntityUid uid, WeaponRegistrationConsoleComponent component, ComponentInit args)
    {
        // Register the slot so click-to-insert and use-to-eject work.
        _itemSlots.AddItemSlot(uid, WeaponSlotId, component.WeaponSlot);
    }
}

