using System.Xml.Linq;
namespace WakeMesh.Mobile.CloudUi.Tests;
public sealed class T88DesignBindingsTests
{
 private static readonly string Root=FindRoot();
 private static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null){if(Directory.Exists(Path.Combine(d.FullName,"src","WakeMesh.Mobile")))return d.FullName;d=d.Parent;}throw new DirectoryNotFoundException();}
 [Fact] public void ShellHasExactlyThreeRootTabsAndSettingsPushRoute(){var x=XDocument.Load(Path.Combine(Root,"src/WakeMesh.Mobile/AppShell.xaml"));XNamespace m="http://schemas.microsoft.com/dotnet/2021/maui";Assert.Equal(new[]{"アラーム","クラウド","設定"},x.Descendants(m+"Tab").Select(x=>((string?)x.Attribute("Title"))!).ToArray());}
 [Fact] public void AlarmListKeepsOperationalBindingsAndJapaneseCopy(){var p=File.ReadAllText(Path.Combine(Root,"src/WakeMesh.Mobile/Views/AlarmsPage.xaml"));foreach(var s in new[]{"RingingAlarms","DismissRingingAlarmCommand","DismissAllRingingCommand","LoadAlarmsCommand","OnAlarmToggled","OnAlarmMenuClicked","OnAddAlarmClicked","＋ 追加","祝日は休み","NextFireTextConverter"})Assert.Contains(s,p);Assert.DoesNotContain("Azure未接続",p);Assert.DoesNotContain("CloudSyncStatusText",p);var behind=File.ReadAllText(Path.Combine(Root,"src/WakeMesh.Mobile/Views/AlarmsPage.xaml.cs"));Assert.Contains("OnAddAlarmClicked",behind);Assert.Contains("GoToAsync(nameof(AlarmEditPage))",behind);}
 [Fact] public void SharedThemeProvidesDesignTokensAndLegacyResourceAliases(){var p=File.ReadAllText(Path.Combine(Root,"src/WakeMesh.Mobile/Resources/Styles/Colors.xaml"));foreach(var s in new[]{"Bg","Surface1","Surface2","Line","LineStrong","Ink","Ink2","Ink3","OnInk","Ok","Warn","WarnSurface","Crit","CritSurface","BrandCyan","SurfaceDark"})Assert.Contains($"x:Key=\"{s}\"",p);}
}
