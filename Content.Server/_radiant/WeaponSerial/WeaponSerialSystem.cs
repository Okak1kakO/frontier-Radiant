using System.Linq;
using Content.Server.CartridgeLoader;
using Content.Shared._NF.Weapons.Rarity;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Examine;

using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._radiant.WeaponSerial;
using Content.Shared._radiant.WeaponSerial.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;
namespace Content.Server._radiant.WeaponSerial;

/// <summary>
///     Server side of the weapon serial number system.
///     - Weapons from vending machines and uplinks automatically get a serial
///       number via TryAssignSerial. It is called from the sale points.
///     - The database is stored only in server memory and only for the round.
///       It is cleared on RoundRestartCleanupEvent. The full SQL database is untouched.
///     - The registration console automatically registers weapons inserted into it.
/// </summary>
public sealed partial class WeaponSerialSystem : SharedWeaponSerialSystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly CartridgeLoaderSystem _cartridge = default!;

    // Round-scoped database. Serial number -> entry about the registered weapon.
    private readonly Dictionary<string, WeaponSerialEntry> _registry = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<WeaponSerialComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<WeaponRegistrationConsoleComponent, EntInsertedIntoContainerMessage>(OnWeaponInserted);

        // OSK database cartridge (the renamed "wanted list" cartridge).
        // Subscribed through its own component: the directed event bus allows a
        // single handler per (component, event) pair, and CriminalRecordsSystem
        // already owns (WantedListCartridgeComponent, CartridgeUiReadyEvent).
        SubscribeLocalEvent<WeaponRegistryCartridgeComponent, CartridgeUiReadyEvent>(OnCartridgeUiReady);
        SubscribeLocalEvent<WeaponRegistryCartridgeComponent, WeaponRegistryUiMessageEvent>(OnRegistryMessage);
    }


    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // New round means new database. All entries are wiped. Local only, no SQL.
        _registry.Clear();
    }
    /// <summary>
    ///     Shows the serial number when the weapon is examined, if any.



    /// </summary>
    private void OnExamined(EntityUid uid, WeaponSerialComponent component, ExaminedEvent args)
    {
        if (component.SerialNumber != null)
            args.PushMarkup(Loc.GetString("weapon-serial-examine", ("serial", component.SerialNumber)));
    }

    /// <summary>
    ///     Issues a serial number to the weapon, if it does not have one yet.



    ///     Called from vending machines and uplinks when an item is sold.



    /// </summary>
    public bool TryAssignSerial(EntityUid uid)
    {
        // Only work with firearms, i.e. GunComponent.



        if (!HasComp<GunComponent>(uid))
            return false;
        if (TryComp<WeaponSerialComponent>(uid, out var comp) && comp.SerialNumber != null)
            return false; // already has a number, do not reissue
        comp = EnsureComp<WeaponSerialComponent>(uid);
        comp.SerialNumber = GenerateSerial();
        Dirty(uid, comp);
        return true;

    }

    /// <summary>
    ///     Registers the weapon in the round database. If the weapon has no serial
    ///     number yet, one is issued. Returns the serial number, or null when this
    ///     is not a weapon.


    /// </summary>
    public string? RegisterWeapon(EntityUid weaponUid)
    {
        if (!HasComp<GunComponent>(weaponUid))
            return null;
        var comp = EnsureComp<WeaponSerialComponent>(weaponUid);
        if (comp.SerialNumber == null)
            comp.SerialNumber = GenerateSerial();
        // The number is guaranteed to exist below.



        var serial = comp.SerialNumber!;
        // Write the weapon into the round-scoped local database.



        // Rarity comes from the existing NF system; weapons without it count as Common.
        var rarity = TryComp<RareWeaponComponent>(weaponUid, out var rare)
            ? rare.Rarity
            : WeaponRarity.Common;

        // Re-registering must not wipe an owner name entered by a player earlier.
        _registry[serial] = new WeaponSerialEntry(serial,
            MetaData(weaponUid).EntityPrototype?.ID ?? "unknown",
            MetaData(weaponUid).EntityName,
            _timing.CurTime,
            rarity,
            _registry.TryGetValue(serial, out var old) ? old.Owner : null);
        Dirty(weaponUid, comp);

        // Push the fresh registry to every OSK database cartridge so the list updates live.
        var query = EntityQueryEnumerator<WeaponRegistryCartridgeComponent>();
        while (query.MoveNext(out var cartridgeUid, out _))
            UpdateCartridgeUi(cartridgeUid);

        return serial;
    }
    /// <summary>
    ///     A weapon was inserted into the console. Register it automatically.



    /// </summary>
    private void OnWeaponInserted(EntityUid uid, WeaponRegistrationConsoleComponent component, EntInsertedIntoContainerMessage args)
    {
        // The event fires for any container insert on the console, filter by our slot.



        if (component.WeaponSlot.ID != args.Container.ID)
            return;
        var weapon = component.WeaponSlot.Item;

        if (weapon == null)
            return;
        var serial = RegisterWeapon(weapon.Value);
        if (serial == null)
        {
            _popup.PopupEntity(Loc.GetString("weapon-registration-not-weapon"), uid);
            return;
        }
        _popup.PopupEntity(Loc.GetString("weapon-registration-success", ("serial", serial)), uid);
        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Machines/printer.ogg"), uid);
    }
    /// <summary>
    ///     The OSK database cartridge UI is ready: send it the current registry.
    /// </summary>
    private void OnCartridgeUiReady(Entity<WeaponRegistryCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateCartridgeUi(ent);
    }

    /// <summary>
    ///     A player entered an owner name for a registered weapon. It is stored in
    ///     the round-scoped registry entry, not on the weapon itself.
    /// </summary>
    private void OnRegistryMessage(EntityUid uid, WeaponRegistryCartridgeComponent component, WeaponRegistryUiMessageEvent args)
    {
        if (_registry.TryGetValue(args.Serial, out var entry))
            _registry[args.Serial] = entry with { Owner = args.Owner };

        UpdateCartridgeUi(uid);
    }

    /// <summary>
    ///     Sends the whole registry snapshot to the cartridge loader UI.
    /// </summary>
    private void UpdateCartridgeUi(EntityUid cartridgeUid)
    {
        // The cartridge knows its loader (the PDA itself) only through this component.
        if (!TryComp<CartridgeComponent>(cartridgeUid, out var cartridge)
            || cartridge.LoaderUid is not { } loaderUid)
            return;

        var entries = _registry.Values
            .OrderBy(e => e.SerialNumber)
            .Select(e => new WeaponRegistryEntry(e.SerialNumber, e.WeaponName, e.Rarity, e.Owner))
            .ToList();

        _cartridge.UpdateCartridgeUiState(loaderUid, new WeaponRegistryUiState(entries));
    }

    /// <summary>
    ///     Generates a serial number in the CON-1234-5678 format.
    /// </summary>
    private string GenerateSerial()
    {
        return $"CON-{_random.Next(0, 10000):D4}-{_random.Next(0, 10000):D4}";
    }
}

/// <summary>
///     Entry about a registered weapon in the round local database.
/// </summary>
public sealed record WeaponSerialEntry(
    string SerialNumber,
    string PrototypeId,
    string WeaponName,
    TimeSpan RegisteredAt,
    WeaponRarity Rarity = WeaponRarity.Common,
    string? Owner = null);

