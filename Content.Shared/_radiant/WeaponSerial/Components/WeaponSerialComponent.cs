using Robust.Shared.GameStates;

namespace Content.Shared._radiant.WeaponSerial.Components;



/// <summary>
///     Weapon serial number. This component is only added to a weapon once
///     a serial number has been issued (vending machine / uplink) or the weapon
///     has been registered (registration console).
///     The absence of this component means the weapon has no serial number.

/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class WeaponSerialComponent : Component
{
    /// <summary>
    ///     The weapon's serial number, null means no number has been issued yet.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? SerialNumber;
}
