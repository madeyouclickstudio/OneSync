using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OneSync.Config;

namespace OneSync.Setup;

/// <summary>
/// Modal dialog for adding or editing a single drive entry. Used by the
/// SetupWizardForm's drive list. Returns the populated DriveConfig via the
/// Result property when DialogResult == OK.
/// </summary>
internal sealed class DriveEditDialog : Form
{
    private const string DefaultSharePointLibrary = "Shared Documents";

    private readonly HashSet<string> _lettersInUseByOthers;
    private readonly ComboBox _letterBox;
    private readonly TextBox _labelBox;
    private readonly RadioButton _typeOneDriveRadio;
    private readonly RadioButton _typeSharePointRadio;
    private readonly Label _siteUrlLabel;
    private readonly TextBox _siteUrlBox;
    private readonly Label _libraryLabel;
    private readonly TextBox _libraryBox;
    private readonly Label _statusLabel;
    private readonly Button _okButton;

    public DriveConfig Result { get; private set; } = new();

    public DriveEditDialog(DriveConfig? existing, IEnumerable<string> existingLetters, bool isEdit)
    {
        // When editing, the row's own letter is fine to keep; only other rows' letters are
        // "in use" from this dialog's perspective.
        var allOtherLetters = existingLetters
            .Where(l => existing is null || !string.Equals(l, existing.Letter, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.ToUpperInvariant());
        _lettersInUseByOthers = new HashSet<string>(allOtherLetters, StringComparer.OrdinalIgnoreCase);

        Text = isEdit ? "Edit drive" : "Add drive";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(420, 340);

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try { Icon = new Icon(iconPath); } catch { }
        }

        Controls.Add(new Label { Text = "Drive letter", Location = new Point(20, 20), AutoSize = true });
        _letterBox = new ComboBox
        {
            Location = new Point(20, 40),
            Size = new Size(80, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        // A-Z minus C (system drive on virtually every Windows install). Letters
        // already used by other drives in the wizard list are skipped so the user
        // can't pick a duplicate.
        for (char c = 'A'; c <= 'Z'; c++)
        {
            if (c == 'C') continue;
            var s = c.ToString();
            if (_lettersInUseByOthers.Contains(s)) continue;
            _letterBox.Items.Add(s);
        }
        Controls.Add(_letterBox);

        Controls.Add(new Label { Text = "Display label", Location = new Point(120, 20), AutoSize = true });
        _labelBox = new TextBox
        {
            Location = new Point(120, 40),
            Size = new Size(280, 24)
        };
        _labelBox.TextChanged += (_, _) => UpdateValidation();
        Controls.Add(_labelBox);

        Controls.Add(new Label { Text = "Drive type", Location = new Point(20, 78), AutoSize = true });
        _typeOneDriveRadio = new RadioButton
        {
            Text = "Personal OneDrive",
            Location = new Point(20, 98),
            AutoSize = true,
            Checked = true
        };
        _typeSharePointRadio = new RadioButton
        {
            Text = "SharePoint library",
            Location = new Point(220, 98),
            AutoSize = true
        };
        _typeOneDriveRadio.CheckedChanged += (_, _) => UpdateTypeFields();
        _typeSharePointRadio.CheckedChanged += (_, _) => UpdateTypeFields();
        Controls.Add(_typeOneDriveRadio);
        Controls.Add(_typeSharePointRadio);

        _siteUrlLabel = new Label { Text = "Site URL", Location = new Point(20, 138), AutoSize = true };
        Controls.Add(_siteUrlLabel);
        _siteUrlBox = new TextBox
        {
            Location = new Point(20, 158),
            Size = new Size(380, 24),
            PlaceholderText = "https://yourtenant.sharepoint.com/sites/teamname"
        };
        _siteUrlBox.TextChanged += (_, _) => UpdateValidation();
        Controls.Add(_siteUrlBox);

        _libraryLabel = new Label { Text = "Library name", Location = new Point(20, 196), AutoSize = true };
        Controls.Add(_libraryLabel);
        _libraryBox = new TextBox
        {
            Location = new Point(20, 216),
            Size = new Size(380, 24),
            Text = DefaultSharePointLibrary
        };
        _libraryBox.TextChanged += (_, _) => UpdateValidation();
        Controls.Add(_libraryBox);

        _statusLabel = new Label
        {
            Location = new Point(20, 250),
            Size = new Size(380, 36),
            ForeColor = Color.Firebrick,
            AutoSize = false
        };
        Controls.Add(_statusLabel);

        _okButton = new Button
        {
            Text = isEdit ? "Update" : "Add",
            Location = new Point(320, 296),
            Size = new Size(80, 30)
        };
        _okButton.Click += OkButton_Click;
        Controls.Add(_okButton);
        AcceptButton = _okButton;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(230, 296),
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancelButton);
        CancelButton = cancelButton;

        // Populate from existing config (edit mode) or sensible defaults (add mode).
        if (existing is not null)
        {
            if (_letterBox.Items.Count > 0)
            {
                var idx = _letterBox.Items.IndexOf(existing.Letter.ToUpperInvariant());
                if (idx < 0)
                {
                    // The current row's letter isn't in the combo because it equals
                    // its own value (we filtered above). Add it back to the front so
                    // edit mode is consistent.
                    _letterBox.Items.Insert(0, existing.Letter.ToUpperInvariant());
                    idx = 0;
                }
                _letterBox.SelectedIndex = idx;
            }
            _labelBox.Text = existing.Label;
            _typeOneDriveRadio.Checked = existing.IsOneDrive;
            _typeSharePointRadio.Checked = existing.IsSharePoint;
            _siteUrlBox.Text = existing.SiteUrl ?? string.Empty;
            _libraryBox.Text = existing.LibraryName ?? DefaultSharePointLibrary;
        }
        else
        {
            if (_letterBox.Items.Count > 0)
            {
                // Default to H if available, otherwise the first free letter.
                var hIdx = _letterBox.Items.IndexOf("H");
                _letterBox.SelectedIndex = hIdx >= 0 ? hIdx : 0;
            }
        }

        UpdateTypeFields();
        UpdateValidation();
    }

    private void UpdateTypeFields()
    {
        var sp = _typeSharePointRadio.Checked;
        _siteUrlLabel.Enabled = sp;
        _siteUrlBox.Enabled = sp;
        _libraryLabel.Enabled = sp;
        _libraryBox.Enabled = sp;
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        var letterOk = _letterBox.SelectedItem is not null;
        var labelOk = !string.IsNullOrWhiteSpace(_labelBox.Text);
        var spOk = !_typeSharePointRadio.Checked
            || (LooksLikeSharePointUrl(_siteUrlBox.Text) && !string.IsNullOrWhiteSpace(_libraryBox.Text));

        _okButton.Enabled = letterOk && labelOk && spOk;

        if (!letterOk) { _statusLabel.Text = "Pick a drive letter."; return; }
        if (!labelOk) { _statusLabel.Text = "Enter a display label."; return; }
        if (_typeSharePointRadio.Checked && !LooksLikeSharePointUrl(_siteUrlBox.Text))
        { _statusLabel.Text = "Site URL must look like https://<tenant>.sharepoint.com/sites/<site>."; return; }
        if (_typeSharePointRadio.Checked && string.IsNullOrWhiteSpace(_libraryBox.Text))
        { _statusLabel.Text = "Library name is required for SharePoint drives."; return; }
        _statusLabel.Text = string.Empty;
    }

    private static bool LooksLikeSharePointUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase);
    }

    private void OkButton_Click(object? sender, EventArgs e)
    {
        var letter = ((string)_letterBox.SelectedItem!).ToUpperInvariant();
        var sp = _typeSharePointRadio.Checked;
        Result = new DriveConfig
        {
            Letter = letter,
            Label = _labelBox.Text.Trim(),
            Type = sp ? "sharepoint" : "onedrive",
            RemotePath = "/",
            SyncMode = "bidirectional",
            Priority = 100,
            SiteUrl = sp ? _siteUrlBox.Text.Trim() : null,
            LibraryName = sp ? _libraryBox.Text.Trim() : null,
            FolderRedirection = sp
                ? null
                : new List<string> { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos" }
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
