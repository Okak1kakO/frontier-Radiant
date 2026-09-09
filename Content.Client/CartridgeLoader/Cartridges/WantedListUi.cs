using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client.CartridgeLoader.Cartridges;

public sealed partial class WantedListUi : UIFragment
{
    private WantedListUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new WantedListUiFragment();

        // The player manually entered an owner name: wrap it into a CartridgeUiMessage
        // so the loader relays it to the server-side cartridge system.
        _fragment.OnOwnerSaved += (serial, owner) =>
        {
            var registryMessage = new WeaponRegistryUiMessageEvent(serial, owner);
            var message = new CartridgeUiMessage(registryMessage);
            userInterface.SendMessage(message);
        };

        // Owner name save and registry refresh both go through CartridgeUiMessage,
        // which the loader relays to the server-side cartridge system. An empty
        // serial means "send me the current registry"; it covers both the initial
        // page and the switch to the registry page.
        _fragment.OnRegistryRefresh += () =>
        {
            var refreshMessage = new CartridgeUiMessage(new WeaponRegistryUiMessageEvent(string.Empty, null));
            userInterface.SendMessage(refreshMessage);
        };

        // Every time the program UI is (re)shown, ask the server for the current
        // snapshot. This covers both a brand-new window open and the loader re-sending
        // its own UI state while this program stays active.
        var initialRefreshMessage = new CartridgeUiMessage(new WeaponRegistryUiMessageEvent(string.Empty, null));
        userInterface.SendMessage(initialRefreshMessage);
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        switch (state)
        {
            case WantedListUiState cast:
                _fragment?.UpdateState(cast.Records);
                break;
            case WeaponRegistryUiState registry:
                _fragment?.UpdateRegistry(registry.Entries);
                break;
        }
    }
}
