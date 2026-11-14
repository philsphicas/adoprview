using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using AdoPrView;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

static class ExplainNativeDemo
{
    // Opt-in: real inference, but only synthetic ADO responses and a fresh native browser profile.
    public static Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "adoprview-explain-demo-" + Guid.NewGuid().ToString("N"));
            using var fixture = new ExplainTests.Fixture(dependencyOnly: true);
            var model = new ExplainModel(Path.Combine(directory, "copilot"));
            var service = new ExplainService((_, ct) => fixture.Capture(ct), model);
            var web = new WebView2();
            var window = new Window { Title = "ADO PR View - real Copilot / synthetic PR demonstration", Width = 1250, Height = 1000, Content = web };
            window.Loaded += async (_, _) =>
            {
                Exception? failure = null;
                CoreWebView2Environment? environment = null;
                try
                {
                    environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory, "browser"));
                    await web.EnsureCoreWebView2Async(environment);
                    var core = web.CoreWebView2;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.Deny);
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += (_, e) =>
                    {
                        if (new Uri(e.Request.Uri).Host != "app.local")
                            e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Offline fixture: network blocked", "");
                    };
                    core.NewWindowRequested += (_, e) => e.Handled = true;
                    var finished = new TaskCompletionSource<ExplainState>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var streams = 0;
                    Task Post(string json) => window.Dispatcher.InvokeAsync(() => core.PostWebMessageAsJson(json)).Task;
                    var bridge = new Bridge(fixture.Service, fixture.Store, Post, _ => { },
                        explainAction: async (action, p) =>
                        {
                            if (action == "listExplanations") return service.States;
                            if (p["key"]?.GetValue<string>() != fixture.Pr.Key) throw new Exception("Demo key escaped the fixture.");
                            return action == "cancelExplanation"
                                ? await service.CancelAsync(fixture.Pr.Key, p["requestId"]!.GetValue<string>())
                                : service.Start(fixture.Pr.Key);
                        });
                    async Task Push(ExplainState state)
                    {
                        await bridge.PushExplanationAsync(state);
                        if (state.Output.Length > 0 && state.Status == "running") streams++;
                        if (!ExplainService.Active(state.Status)) finished.TrySetResult(state);
                    }
                    service.Changed += state => _ = Push(state);
                    core.WebMessageReceived += async (_, e) =>
                    {
                        if (!PopTarget.IsDashboardSource(e.Source)) return;
                        var text = e.TryGetWebMessageAsString();
                        if (text == "ready") { ready.TrySetResult(); return; }
                        var method = JsonNode.Parse(text)?["method"]?.GetValue<string>();
                        if (method is not ("init" or "explainPr" or "cancelExplanation"))
                            throw new Exception("The offline native demo does not allow dashboard writes or external navigation.");
                        await bridge.HandleAsync(text);
                    };
                    core.Navigate("https://app.local/index.html");
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    var key = JsonSerializer.Serialize(fixture.Pr.Key);
                    await core.ExecuteScriptAsync($$"""
                        document.querySelector('.brand-sub').textContent = 'Real SDK / synthetic PR';
                        S.expanded.add({{key}}); renderRows();
                        document.querySelector('.explanation [data-act="explainPr"]').click();
                        """);
                    var result = await finished.Task.WaitAsync(TimeSpan.FromMinutes(4));
                    if (result.Status != "completed") throw new Exception("Live native explanation failed: " + result.Message);
                    if (streams == 0 || !result.Output.Contains("fixture", StringComparison.OrdinalIgnoreCase) ||
                        !result.Output.Contains("0.30.0") ||
                        !result.Output.Contains("developer", StringComparison.OrdinalIgnoreCase) && !result.Output.Contains("test", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Live result did not establish the dependency's developer/test utility context.");
                    if (result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 80 ||
                        System.Text.RegularExpressions.Regex.Split(result.Output.Trim(), @"(?<=[.!?])\s+(?=[A-Z])").Length > 3 ||
                        result.Output.Contains("\n- ") || result.Output.Contains("##"))
                        throw new Exception("Live summary exceeded three short sentences/80 words or became a report: " + result.Output);
                    if (result.Evidence?.Any(s => s.Contains("README.md")) != true ||
                        !fixture.Urls.Any(s => s.Contains("README.md") && s.Contains("includeContent=true")))
                        throw new Exception("Live result did not inspect repository context beyond the one-line diff.");
                    await Task.Delay(500);
                    var visible = await core.ExecuteScriptAsync("document.querySelector('.explain-output').textContent");
                    if (string.IsNullOrEmpty(JsonSerializer.Deserialize<string>(visible))) throw new Exception("Native bridge did not deliver the visible response.");
                    if (await core.ExecuteScriptAsync("document.querySelector('.explain-details').open") != "false")
                        throw new Exception("Evidence must be collapsed by default.");
                    var output = Environment.GetEnvironmentVariable("ADOPRVIEW_EXPLAIN_SCREENSHOT");
                    if (!string.IsNullOrEmpty(output))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                        await using (var file = File.Create(output)) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                        await core.ExecuteScriptAsync("document.querySelector('.explain-details').open = true");
                        await using (var file = File.Create(Path.ChangeExtension(output, ".evidence.png")))
                            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                        await core.ExecuteScriptAsync("document.querySelector('.explain-details').open = false");
                        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(Bridge.Json) { WriteIndented = true }));
                        window.Width = 930;
                        window.UpdateLayout();
                        await Task.Delay(300);
                        var overflow = await core.ExecuteScriptAsync("document.querySelector('.explain-output').scrollWidth > document.querySelector('.explain-output').clientWidth + 1");
                        if (overflow == "true") throw new Exception("Explanation overflows the narrow dashboard.");
                        await using var narrow = File.Create(Path.ChangeExtension(output, ".narrow.png"));
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, narrow);
                    }
                    Console.WriteLine($"LIVE Explain: {streams} streamed native UI updates; {fixture.Urls.Count} offline GETs; source {result.Revision!.SourceCommit}; base {result.Revision.BaseCommit}.");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { await service.DisposeAsync(); }
                    catch (Exception ex) { failure ??= ex; }
                    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (environment is not null) environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
                    web.Dispose();
                    window.Close();
                    if (environment is not null) await Task.WhenAny(exited.Task, Task.Delay(10000));
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                    catch (IOException ex) { failure ??= new Exception("Could not remove the isolated explanation demo directory.", ex); }
                    if (failure is null) done.TrySetResult(); else done.TrySetException(failure);
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }
}
