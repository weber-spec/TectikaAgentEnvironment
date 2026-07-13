using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.Api.Services;

/// <summary>Wiring for the SSE stack in tests that only need a ChatService to have *a* broadcaster.</summary>
internal static class TestSse
{
    public static SseHub Hub() => new(NullLogger<SseHub>.Instance);

    public static SseConnectionManager Manager(ICosmosDbService cosmos, SseHub? hub = null, RunBoardIndex? index = null) =>
        new(hub ?? Hub(),
            new RunBoardResolver(index ?? new RunBoardIndex(), cosmos, NullLogger<RunBoardResolver>.Instance),
            NullLogger<SseConnectionManager>.Instance);
}
