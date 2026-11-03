using Resurgence.Services;
using Resurgence.Ui;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

public class ModalServiceTests
{
    [Fact]
    public async Task ClosingAConfirmAnswersNo()
    {
        var m = new ModalService();
        var answer = m.Confirm("sure?");
        m.Close();
        Assert.True(answer.IsCompleted);
        Assert.False(await answer);
    }

    [Fact]
    public async Task ReplacingAConfirmAnswersNo()
    {
        var m = new ModalService();
        var answer = m.Confirm("sure?");
        m.Show<AlertDialog>();
        Assert.True(answer.IsCompleted);
        Assert.False(await answer);
    }

    [Fact]
    public async Task TheFirstStartSetupGoesOnWhenThereIsNothingToSetUp()
    {
        Assert.True(await new ModalService().SetUpFirst(new Settings(first: false, error: null)));
        Assert.True(await new ModalService().SetUpFirst(new Settings(first: true, error: "GameSettings.Missing")));
    }

    [Fact]
    public async Task ClosingTheSetupStopsTheStart()
    {
        var m = new ModalService();
        var start = m.SetUpFirst(new Settings(first: true, error: null));
        Assert.Equal(typeof(GameSettingsModal), m.Type);
        m.Close();
        Assert.True(start.IsCompleted);
        Assert.False(await start);
    }

    [Fact]
    public void OnlySomeDialogsCloseOnTheBackdrop()
    {
        var m = new ModalService();
        Assert.False(m.ClosesOnBackdrop);
        m.Show<VideoModal>();
        Assert.True(m.ClosesOnBackdrop);
        m.Show<LanguageModal>();
        Assert.False(m.ClosesOnBackdrop);
    }

    [Theory]
    [InlineData("poll:fast", 2)]
    [InlineData("messages:incoming", 2)]
    [InlineData(null, 30)]
    [InlineData("session:out", 30)]
    public void PollIntervalIsShortOnlyForTheHooksThatWaitForSomething(string? mock, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), new HostMode { Mock = mock }.PollInterval);

    sealed class Settings(bool first, string? error) : IGameSettingsService
    {
        public Task<GameSettings> ReadAsync() => Task.FromResult(new GameSettings(new(false, false, new(1, 1, 1), 0, 0, 0, 0, 0, null, null), [], 0, error));
        public Task<Result> SaveAsync(GameOptions options) => throw new NotSupportedException();
        public Task<bool> FirstStartAsync() => Task.FromResult(first);
    }
}
