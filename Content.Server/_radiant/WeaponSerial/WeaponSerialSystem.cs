using System.Linq;
using Content.Shared._NF.Weapons.Rarity;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Examine;

using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._radiant.WeaponSerial;
using Content.Shared._radiant.WeaponSerial.Components;
using Robust.Server.GameObjects;
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
    [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;

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

        // Note: we deliberately do NOT subscribe to BoundUIOpenedEvent on the loader.
        // The loader BUI (PdaUiKey for PDAs) stores ONE canonical state per key, and
        // PdaSystem refreshes it to a CartridgeLoaderUiState subclass when the window
        // opens. Pushing our WeaponRegistryUiState from an open handler races with that
        // refresh: if we run last, the window opens with our state instead of the
        // program list and looks empty until the cartridge is ejected and reinserted.
        // Instead the client asks for a snapshot (empty-serial message) and the loader
        // raises CartridgeUiReadyEvent whenever the OSK program is activated.
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
        BroadcastRegistry();

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
        // UIReady is raised after the PDA has attached the fragment. Use its
        // authoritative loader directly so this snapshot is written after the
        // PDA's normal state update and cannot be lost during a reopen.
        if (!TryComp(args.Loader, out CartridgeLoaderComponent? loader)
            || !_userInterfaceSystem.HasUi(args.Loader, loader.UiKey))
            return;

        _userInterfaceSystem.SetUiState(
            args.Loader,
            loader.UiKey,
            new WeaponRegistryUiState(BuildRegistrySnapshot()));
    }

    /// <summary>
    ///     A player entered an owner name for a registered weapon. It is stored in
    ///     the round-scoped registry entry, not on the weapon itself.
    /// </summary>
    private void OnRegistryMessage(EntityUid uid, WeaponRegistryCartridgeComponent component, WeaponRegistryUiMessageEvent args)
    {
        // Refresh request: empty serial means "send me the current registry".
        if (string.IsNullOrWhiteSpace(args.Serial))
        {
            // The loader uid comes from the active PDA UI message. Send directly to
            // that loader instead of rediscovering it through cartridge ownership.
            // The latter can be stale when the PDA has just been reopened.
            var loaderUid = GetEntity(args.LoaderUid);
            if (TryComp(loaderUid, out CartridgeLoaderComponent? loader)
                && _userInterfaceSystem.HasUi(loaderUid, loader.UiKey))
            {
                _userInterfaceSystem.SetUiState(
                    loaderUid,
                    loader.UiKey,
                    new WeaponRegistryUiState(BuildRegistrySnapshot()));
            }

            return;
        }

        // Owner save: look up the entry and update it.
        if (_registry.TryGetValue(args.Serial, out var entry))
        {
            _registry[args.Serial] = entry with { Owner = args.Owner };
        }

        // Broadcast the update to every open OSK database window.
        BroadcastRegistry();
    }

    private List<WeaponRegistryEntry> BuildRegistrySnapshot()
    {
        return _registry.Values
            .OrderBy(e => e.SerialNumber, StringComparer.Ordinal)
            .Select(e => new WeaponRegistryEntry(e.SerialNumber, e.WeaponName, e.Rarity, e.Owner))
            .ToList();
    }

    /// <summary>
    ///     Pushes a registry snapshot to one open OSK database window.
    ///     The fragment UI lives inside the loader/PDA window: the client's
    ///     CartridgeLoaderBoundUserInterface forwards any non-CartridgeLoaderUiState
    ///     it receives on the loader's UiKey to the active program fragment. So the
    ///     state is sent to the loader (PDA) with the loader's UiKey.
    ///     The state slot is shared with the loader's own program-list state, so we
    ///     only push while an OSK window is actually open AND displaying this
    ///     cartridge. Pushing to a closed PDA would overwrite its program-list state
    ///     and make the next open show an empty window.
    /// </summary>
    private void PushRegistry(
        EntityUid cartridgeUid,
        EntityUid loaderUid,
        List<WeaponRegistryEntry> entries)
    {
        if (!cartridgeUid.IsValid() || !loaderUid.IsValid())
            return;

        if (!TryComp<CartridgeLoaderComponent>(loaderUid, out var loaderComp))
            return;

        // Only an open window that is currently showing the OSK database needs a
        // live snapshot. On every fresh open the client re-requests the registry
        // itself (empty-serial message / CartridgeUiReadyEvent), so skipping here
        // never leaves a window without data.
        if (loaderComp.ActiveProgram != cartridgeUid)
            return;

        if (!_userInterfaceSystem.IsUiOpen(loaderUid, loaderComp.UiKey))
            return;

        _userInterfaceSystem.SetUiState(loaderUid, loaderComp.UiKey, new WeaponRegistryUiState(entries));
    }

    /// <summary>
    ///     Sends the fresh registry to every currently open OSK database window.
    ///     Installed copies know their loader through CartridgeComponent.LoaderUid;
    ///     a physical cartridge sitting in a loader slot is found through the slot.
    /// </summary>
    private void BroadcastRegistry()
    {
        var entries = BuildRegistrySnapshot();
        var notified = new HashSet<EntityUid>();

        // Cartridges that have the registry component and know their loader directly.
        var query = EntityQueryEnumerator<WeaponRegistryCartridgeComponent, CartridgeComponent>();
        while (query.MoveNext(out var entityUid, out var _, out var cartridge))
        {
            if (cartridge.LoaderUid is { } loaderUid && notified.Add(entityUid))
                PushRegistry(entityUid, loaderUid, entries);
        }

        // Loaders that may have a physical OSK cartridge in their cartridge slot.
        var loaders = EntityQueryEnumerator<CartridgeLoaderComponent>();
        while (loaders.MoveNext(out var loaderUid, out var loader))
        {
            var item = loader.CartridgeSlot.Item;
            if (item == null || !HasComp<WeaponRegistryCartridgeComponent>(item))
                continue;

            var cartridgeUid = item.Value;
            if (notified.Contains(cartridgeUid))
                continue;

            notified.Add(cartridgeUid);
            PushRegistry(cartridgeUid, loaderUid, entries);
        }
    }

    /// <summary>
    ///     Generates a serial number in the CON-1234-5678 format.
    /// </summary>
    private string GenerateSerial()
    {
        // Uniqueness is guaranteed against the registry so one serial can never
        // point at two weapons (that would silently overwrite another entry).
        string serial;
        do
        {
            serial = $"CON-{_random.Next(0, 10000):D4}-{_random.Next(0, 10000):D4}";
        }
        while (_registry.ContainsKey(serial));

        return serial;
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

