using Content.Shared.Damage;
using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Teargas;
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedTeargasSystem))]
public sealed partial class TeargasInjectorComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public float GasPerSecond;

    [DataField, AutoNetworkedField]
    public TimeSpan TimeBetweenGasInjects = TimeSpan.FromSeconds(1);

    [DataField, AutoNetworkedField]
    public bool AffectsDead;

    [DataField, AutoNetworkedField]
    public bool AffectsInfectedNested;

    [DataField, AutoNetworkedField]
    public bool InjectInContact = true;

    [DataField, AutoNetworkedField]
    public TimeSpan DazeTime = TimeSpan.FromSeconds(6);
}
