using Content.Server.Chat.Systems;
using Content.Shared._RMC14.Teargas;

namespace Content.Server._RMC14;

public sealed class TeargasSystem : SharedTeargasSystem
{
    [Dependency] ChatSystem _chat = default!;
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TeargasComponent, TeargasEmoteEvent>(OnEmote);
    }

    public void OnEmote(Entity<TeargasComponent> victim, ref TeargasEmoteEvent args)
    {
        _chat.TryEmoteWithChat(victim, args.Emote);
    }
}
