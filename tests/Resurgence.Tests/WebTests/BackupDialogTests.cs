using Resurgence.Ui;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

public class BackupDialogTests
{
    [Fact]
    public void OfferedThroughTheLastServerDayAndOnlyWithTheLink()
    {
        var brand = new Branding(new() { ["link_backup"] = "https://example.test/backup-win-x64.zip" });
        Assert.True(BackupDialog.Offered(brand, new DateTime(2026, 11, 11, 23, 59, 59, DateTimeKind.Utc)));
        Assert.False(BackupDialog.Offered(brand, new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(BackupDialog.Offered(new Branding(), new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc)));
    }
}
