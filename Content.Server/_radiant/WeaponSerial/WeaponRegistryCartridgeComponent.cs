namespace Content.Server._radiant.WeaponSerial;

/// <summary>
///     Marks the OSK database cartridge as owned by WeaponSerialSystem. It sits on
///     the same entity as WantedListCartridgeComponent (see the cartridge prototype),
///     because the directed event bus allows only ONE handler per (component, event)
///     pair: CriminalRecordsSystem already subscribes (WantedListCartridgeComponent,
///     CartridgeUiReadyEvent), so this system must subscribe through its own component.
/// </summary>
[RegisterComponent]
public sealed partial class WeaponRegistryCartridgeComponent : Component
{
}
