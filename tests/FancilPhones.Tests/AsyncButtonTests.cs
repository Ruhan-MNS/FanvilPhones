#pragma warning disable BL0006 // Test-only renderer inspects the framework's event/render pipeline.
using FancilPhones.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FancilPhones.Tests;

public sealed class AsyncButtonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingActionShowsFeedbackRejectsDuplicateClicksAndClearsOnCompletion(bool fail)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new ButtonRenderer(services);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicks = 0;
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(AsyncButton.Text)] = "Start delivery",
            [nameof(AsyncButton.BusyText)] = "Delivering…",
            [nameof(AsyncButton.OnClick)] = EventCallback.Factory.Create(this, () => { clicks++; return completion.Task; })
        });
        var id = await renderer.Dispatcher.InvokeAsync(() => renderer.MountAsync(new AsyncButton(), parameters));
        var clickId = renderer.Frames(id).Single(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick").AttributeEventHandlerId;
        var first = renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(clickId, null, new MouseEventArgs()));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            var frames = renderer.Frames(id);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text && f.TextContent == "Delivering…");
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "disabled" && Equals(f.AttributeValue, true));
        });
        await renderer.Dispatcher.InvokeAsync(() => renderer.DispatchEventAsync(clickId, null, new MouseEventArgs()));
        Assert.Equal(1, clicks);
        if (fail) completion.SetException(new InvalidOperationException("Simulated action failure"));
        else completion.SetResult();
        var error = await Record.ExceptionAsync(() => first);
        if (fail) Assert.True(error is not null || renderer.Errors.Count == 1);
        else Assert.Null(error);
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            var frames = renderer.Frames(id);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text && f.TextContent == "Start delivery");
            Assert.DoesNotContain(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "disabled" && Equals(f.AttributeValue, true));
        });
    }

    private sealed class ButtonRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Test", true);
        public List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        public async Task<int> MountAsync(IComponent component, ParameterView parameters)
        {
            var id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, parameters);
            return id;
        }
        public RenderTreeFrame[] Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array.Take(frames.Count).ToArray();
        }
    }
}
