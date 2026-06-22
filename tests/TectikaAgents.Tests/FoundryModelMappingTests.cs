using TectikaAgents.AgentRuntime;
using Xunit;

public class FoundryModelMappingTests
{
    [Fact]
    public void SelectChatModels_KeepsChatDeployments_DropsNonChat_DedupesAndTrimsBlanks()
    {
        var deployments = new[]
        {
            new FoundryDeployment("gpt-4o", "ModelDeployment", "gpt-4o", "OpenAI"),
            new FoundryDeployment("text-embedding-3-large", "ModelDeployment", "text-embedding-3-large", "OpenAI"),
            new FoundryDeployment("my-gpt", "ModelDeployment", "gpt-4o", "OpenAI"),   // custom deployment name, chat model
            new FoundryDeployment("gpt-4o", "ModelDeployment", "gpt-4o", "OpenAI"),   // duplicate name
            new FoundryDeployment("  ", "ModelDeployment", "gpt-4o", "OpenAI"),        // blank name -> dropped
            new FoundryDeployment("whisper-1", "ModelDeployment", "whisper", "OpenAI"),// non-chat audio model
            new FoundryDeployment("dall-e-3", "ModelDeployment", "dall-e-3", "OpenAI"),// non-chat image model
        };

        var models = FoundryModelMapping.SelectChatModels(deployments);

        Assert.Equal(new[] { "gpt-4o", "my-gpt" }, models);
    }

    [Fact]
    public void SelectChatModels_UnknownTypeOrPublisher_IsListedNotHidden()
    {
        // A deployment we can't positively classify (no embedding/audio/image marker) is kept.
        var deployments = new[]
        {
            new FoundryDeployment("claude-opus-4-8", null, "claude-opus-4-8", "Anthropic"),
            new FoundryDeployment("some-future-model", "ModelDeployment", null, null),
        };

        var models = FoundryModelMapping.SelectChatModels(deployments);

        Assert.Equal(new[] { "claude-opus-4-8", "some-future-model" }, models);
    }
}
