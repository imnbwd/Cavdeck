using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using Cavdeck.App.Controls;
using Cavdeck.App.Localization;
using Xunit;

namespace Cavdeck.App.Tests;

// WPF Dispatcher and resource loading use process-wide state. These STA tests
// must not race other WPF resource tests in the xUnit assembly.
[CollectionDefinition("Toast WPF dispatcher", DisableParallelization = true)]
public sealed class ToastWpfDispatcherCollection
{
}

[Collection("Toast WPF dispatcher")]
public sealed class ToastServiceTests
{
    [Fact]
    public void Default_durations_preserve_existing_user_experience()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), ToastService.DefaultDuration(ToastTone.Information));
        Assert.Equal(TimeSpan.FromSeconds(4), ToastService.DefaultDuration(ToastTone.Success));
        Assert.Equal(TimeSpan.FromSeconds(5.5), ToastService.DefaultDuration(ToastTone.Warning));
        Assert.Equal(TimeSpan.FromSeconds(7), ToastService.DefaultDuration(ToastTone.Error));
    }

    [Fact]
    public void Oldest_normal_is_evicted_and_released_before_important_errors()
    {
        RunOnSta(service =>
        {
            service.Show(new ToastRequest { Title = "info", Duration = Timeout.InfiniteTimeSpan });
            var info = service.Items.Single();
            service.Show(new ToastRequest { Title = "success", Tone = ToastTone.Success });
            var success = service.Items.Last();
            service.Show(new ToastRequest { Title = "error A", Tone = ToastTone.Error });
            var errorA = service.Items.Last();
            service.Show(new ToastRequest { Title = "error B", Tone = ToastTone.Error });
            var errorB = service.Items.Last();

            service.Show(new ToastRequest { Title = "next info" });
            Assert.True(info.IsDisposed);
            Assert.False(info.IsTimeoutActive);
            Assert.DoesNotContain(info, service.Items);
            Assert.Contains(errorA, service.Items);
            Assert.Contains(errorB, service.Items);
            Assert.Equal(4, service.Items.Count);

            service.Show(new ToastRequest { Title = "error C", Tone = ToastTone.Error });
            Assert.True(success.IsDisposed);
            Assert.Contains(errorA, service.Items);
            Assert.Contains(errorB, service.Items);

            service.Show(new ToastRequest { Title = "error D", Tone = ToastTone.Error });
            Assert.All(service.Items, item => Assert.Equal(ToastPriority.High, item.Priority));
            var protectedIds = service.Items.Select(item => item.Id).ToArray();

            service.Show(new ToastRequest { Title = "routine", Tone = ToastTone.Success });
            Assert.Equal(protectedIds, service.Items.Select(item => item.Id).ToArray());

            service.Show(new ToastRequest { Title = "urgent", Tone = ToastTone.Error });
            Assert.Equal(4, service.Items.Count);
            Assert.True(errorA.IsDisposed);
            Assert.Contains(service.Items, item => item.Title == "urgent");
        });
    }

    [Fact]
    public void Explicit_priority_protects_an_actionable_warning()
    {
        RunOnSta(service =>
        {
            service.Show(new ToastRequest
            {
                Title = "needs attention",
                Tone = ToastTone.Warning,
                Priority = ToastPriority.High
            });
            var warning = service.Items.Single();
            service.Show(new ToastRequest { Title = "routine 1" });
            service.Show(new ToastRequest { Title = "routine 2" });
            service.Show(new ToastRequest { Title = "routine 3" });
            service.Show(new ToastRequest { Title = "routine 4" });

            Assert.Contains(warning, service.Items);
            Assert.False(warning.IsDisposed);
            Assert.Equal(4, service.Items.Count);
        });
    }

    [Fact]
    public void Hover_pause_and_resume_use_remaining_time_without_reviving_closed_toasts()
    {
        RunOnSta(service =>
        {
            service.Show(new ToastRequest
            {
                Title = "short",
                Duration = TimeSpan.FromMilliseconds(100)
            });
            var item = service.Items.Single();
            Pump(TimeSpan.FromMilliseconds(20));
            item.Pause();
            Assert.False(item.IsTimeoutActive);

            Pump(TimeSpan.FromMilliseconds(160));
            Assert.Single(service.Items);

            item.Resume();
            item.Resume();
            Assert.True(item.IsTimeoutActive);
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.Empty(service.Items);
            Assert.True(item.IsDisposed);
            Assert.False(item.IsTimeoutActive);

            item.Resume();
            Assert.False(item.IsTimeoutActive);
        });
    }

    [Fact]
    public void Manual_dismiss_is_idempotent_and_stops_timeout_and_close_timers()
    {
        RunOnSta(service =>
        {
            service.Show(new ToastRequest { Title = "close me", Duration = TimeSpan.FromSeconds(3) });
            var item = service.Items.Single();
            item.Dismiss();
            item.Dismiss();
            item.Pause();
            item.Resume();

            Assert.True(item.IsClosing);
            Assert.False(item.IsTimeoutActive);
            Pump(TimeSpan.FromMilliseconds(220));
            Assert.Empty(service.Items);
            Assert.True(item.IsDisposed);

            service.Show(new ToastRequest { Title = "another" });
            var remaining = service.Items.Single();
            service.Dispose();
            Assert.Empty(service.Items);
            Assert.True(remaining.IsDisposed);
            Assert.False(remaining.IsTimeoutActive);
            service.Show(new ToastRequest { Title = "ignored after dispose" });
            Assert.Empty(service.Items);
        });
    }

    [Fact]
    public void Worker_thread_calls_are_posted_without_synchronously_blocking()
    {
        RunOnSta(service =>
        {
            var show = Task.Run(() => service.Show(new ToastRequest { Title = "from worker" }));
            Assert.True(show.Wait(TimeSpan.FromSeconds(2)));
            Assert.Empty(service.Items);

            Pump(TimeSpan.FromMilliseconds(40));
            var item = Assert.Single(service.Items);
            var dismiss = Task.Run(() => service.Dismiss(item.Id));
            Assert.True(dismiss.Wait(TimeSpan.FromSeconds(2)));

            Pump(TimeSpan.FromMilliseconds(220));
            Assert.Empty(service.Items);
            Assert.True(item.IsDisposed);
        });
    }

    [Fact]
    public void Action_is_one_shot_and_exceptions_do_not_escape_to_the_dispatcher()
    {
        RunOnSta(service =>
        {
            var attempts = 0;
            service.Show(new ToastRequest
            {
                Title = "action",
                ActionLabel = "Open",
                Action = () =>
                {
                    attempts++;
                    throw new InvalidOperationException("sanitized by diagnostic logger");
                }
            });

            var item = service.Items.Single();
            item.ActionCommand!.Execute(null);
            item.ActionCommand.Execute(null);
            Assert.Equal(1, attempts);
            Pump(TimeSpan.FromMilliseconds(220));
            Assert.Empty(service.Items);
        });
    }

    [Fact]
    public void Error_toast_copies_only_the_sanitized_diagnostic_report_without_dismissing()
    {
        string? copied = null;
        RunOnSta(service =>
        {
            service.Show(new ToastRequest
            {
                Title = "failed to upload /private/customer.txt",
                Message = "Never include raw exception text or signed URLs",
                Tone = ToastTone.Error
            });
            var error = Assert.Single(service.Items);
            Assert.True(error.HasCopy);
            Assert.Contains("Error", error.AccessibleAnnouncement);
            error.CopyCommand!.Execute(null);

            Assert.Equal("Cavdeck diagnostic report: safe IDs only", copied);
            Assert.DoesNotContain("customer", copied);
            Assert.Equal("Diagnostic report copied.", error.CopyFeedback);
            Assert.False(error.IsClosing);
            Assert.Single(service.Items);

            service.Show(new ToastRequest { Title = "success", Tone = ToastTone.Success });
            Assert.False(service.Items.Last().HasCopy);
        }, () => new ToastService(new LocalizationService(),
            text => copied = text, () => "Cavdeck diagnostic report: safe IDs only"));
    }

    [Fact]
    public void Clipboard_failure_gives_feedback_without_crashing_or_dismissing()
    {
        RunOnSta(service =>
        {
            service.Show(new ToastRequest { Title = "failure", Tone = ToastTone.Error });
            var error = Assert.Single(service.Items);
            error.CopyCommand!.Execute(null);

            Assert.Equal("Could not copy the report.", error.CopyFeedback);
            Assert.False(error.IsClosing);
            Assert.Single(service.Items);
        }, () => new ToastService(new LocalizationService(),
            _ => throw new System.Runtime.InteropServices.ExternalException("clipboard busy"),
            () => "safe"));
    }

    [Fact]
    public void Toast_actions_close_once_but_copy_stays_and_visible_labels_retranslate()
    {
        LocalizationService? texts = null;
        RunOnSta(service =>
        {
            var opened = 0;
            service.Show(new ToastRequest
            {
                Title = "Transfer failed",
                Tone = ToastTone.Error,
                ActionLabelKey = "Toast_ViewFailedTransfers",
                Action = () => opened++,
                Duration = Timeout.InfiniteTimeSpan
            });

            var item = Assert.Single(service.Items);
            Assert.True(item.HasAction);
            Assert.Equal("View failed transfers", item.ActionLabel);
            Assert.Contains("Transfer failed", item.CloseLabel);
            Assert.True(item.TryMarkAnnounced());
            Assert.False(item.TryMarkAnnounced());
            texts!.ChangeLanguage("de-DE");
            Assert.Equal("Fehler", item.AccessibleAnnouncement.Split(" — ")[0]);
            Assert.Equal("Fehlgeschlagene Übertragungen anzeigen", item.ActionLabel);
            Assert.Equal("Diagnose kopieren", item.CopyLabel);
            Assert.Contains("Benachrichtigung schließen", item.CloseLabel);

            item.CopyCommand!.Execute(null);
            Assert.Equal("Diagnosebericht kopiert.", item.CopyFeedback);
            Assert.False(item.IsClosing);
            item.ActionCommand!.Execute(null);
            item.ActionCommand.Execute(null);
            Assert.Equal(1, opened);
            Assert.True(item.IsClosing);
            texts.ChangeLanguage("en-US");
        }, () => new ToastService(texts = new LocalizationService(),
            _ => { }, () => "safe"));
    }

    private static void RunOnSta(Action<ToastService> assertion,
        Func<ToastService>? createService = null)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var service = createService?.Invoke() ?? new ToastService(new LocalizationService());
                assertion(service);
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Toast dispatcher test timed out");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        EventHandler tick = (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Tick += tick;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Tick -= tick;
    }
}
