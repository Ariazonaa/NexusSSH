using System.Text;
using NexusSSH.Core.Mock;
using NexusSSH.Core.Models;
using NexusSSH.Core.Services;

namespace NexusSSH.Core.Tests;

/// <summary>Vorschau im Dateibrowser: was sich zeigen lässt, und wie der Inhalt gelesen wird.</summary>
public sealed class PreviewTests
{
    [Theory]
    [InlineData("notes.txt", 100, PreviewKind.Text)]
    [InlineData("Makefile", 100, PreviewKind.Text)]
    [InlineData("app.log", RemotePreview.MaxTextSize, PreviewKind.Text)]
    [InlineData("app.log", RemotePreview.MaxTextSize + 1, PreviewKind.None)]
    [InlineData("Logo.PNG", 5_000_000, PreviewKind.Image)]
    [InlineData("photo.jpeg", RemotePreview.MaxImageSize + 1, PreviewKind.None)]
    // Ein SVG ist Text mit Skripten darin – gezeigt wird es als Text, nicht gezeichnet.
    [InlineData("drawing.svg", 100, PreviewKind.Text)]
    public void What_can_be_shown_goes_by_extension_and_size(string name, long size, PreviewKind expected)
        => Assert.Equal(expected, RemotePreview.KindOf(name, size));

    [Fact]
    public void Text_is_read_as_utf8_or_else_western_and_shown_without_control_characters()
    {
        var (text, truncated) = RemotePreview.ReadText([0xEF, 0xBB, 0xBF, .. "Grüße\r\n\tzweite\u001b[31m Zeile‮\n"u8]).GetValueOrDefault();
        Assert.False(truncated);
        Assert.Equal(2, text.Split('\n').Length);
        Assert.StartsWith("Grüße\n    zweite", text);
        Assert.DoesNotContain(text, character => character is '\u001b' or '‮' or '\r' or '\t' or '﻿');

        // Kein gültiges UTF-8: dann die westeuropäische Kodierung.
        Assert.Equal("Büro", RemotePreview.ReadText([0x42, 0xFC, 0x72, 0x6F])!.Value.Text);

        // Eine Binärdatei ist kein Text.
        Assert.Null(RemotePreview.ReadText([0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01]));

        // Von einer langen Datei nur der Anfang.
        var big = RemotePreview.ReadText(Encoding.UTF8.GetBytes(new string('x', RemotePreview.MaxShownCharacters + 10)))!.Value;
        Assert.True(big.Truncated);
        Assert.Equal(RemotePreview.MaxShownCharacters, big.Text.Length);
        Assert.Equal(string.Empty, RemotePreview.ReadText([])!.Value.Text);
    }

    [Fact]
    public async Task Fetching_waits_for_the_download_and_names_why_it_failed()
    {
        using var queue = new MockTransferQueue();
        var files = new MockRemoteFileSystem("/var/www", queue).File("/var/www/notes.txt", 10, hoursAgo: 1);
        var folder = Path.Combine(Path.GetTempPath(), "nexus-preview-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fetching = RemotePreview.FetchAsync(queue, files, "/var/www/notes.txt", folder);
            Assert.False(fetching.IsCompleted);
            var download = queue.Items.Last();
            queue.Complete(download.Id);
            var (local, failure) = await fetching;
            Assert.Equal((Path.Combine(folder, "notes.txt"), null), (local, failure));

            var failing = RemotePreview.FetchAsync(queue, files, "/var/www/notes.txt", folder);
            queue.Fail(queue.Items.Last().Id, "permission_denied: Permission denied");
            Assert.Equal((null, "permission_denied: Permission denied"), await failing);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
