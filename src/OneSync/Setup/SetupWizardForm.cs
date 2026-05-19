using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using OneSync.Config;

namespace OneSync.Setup;

/// <summary>
/// First-run setup wizard. Triggered by Program.cs when the loaded config.json
/// has placeholder tenantId/clientId GUIDs. Collects the Entra ID app values
/// and the user's initial drive layout, then writes a user-writable config at
/// %LOCALAPPDATA%\OneSync\config.json so the next startup picks it up via the
/// reordered ConfigLoader.DefaultPaths.
/// </summary>
internal sealed class SetupWizardForm : Form
{
    private const string DeploymentDocUrl =
        "https://github.com/madeyouclickstudio/OneSync/blob/main/DEPLOYMENT.md#tenant-prep-checklist";

    private readonly string _templatePath;
    private readonly List<DriveConfig> _drives = new();

    private readonly TextBox _tenantIdBox;
    private readonly TextBox _clientIdBox;
    private readonly ListView _drivesList;
    private readonly Button _editDriveButton;
    private readonly Button _removeDriveButton;
    private readonly Label _statusLabel;
    private readonly Button _saveButton;

    /// <summary>Set after a successful Save. Caller can reload from this path.</summary>
    public string? WrittenConfigPath { get; private set; }

    public SetupWizardForm(string templatePath)
    {
        _templatePath = templatePath;

        Text = "OneSync Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(620, 620);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
        {
            try { Icon = new Icon(iconPath); } catch { /* non-fatal */ }
        }

        Controls.Add(new Label
        {
            Text = "Connect OneSync to your Microsoft 365 tenant",
            Font = new Font("Segoe UI Semibold", 12F),
            Location = new Point(20, 15),
            AutoSize = true
        });

        Controls.Add(new Label
        {
            Text = "Before you start, create an Entra ID (Azure AD) app registration with:",
            Location = new Point(20, 50),
            Size = new Size(580, 18),
            AutoSize = false
        });
        Controls.Add(new Label
        {
            Text =
                "  •  Delegated permissions:  Files.ReadWrite,  Sites.ReadWrite.All,  offline_access,  User.Read\n" +
                "  •  Admin consent granted for those permissions\n" +
                "  •  \"Allow public client flows\" set to Yes  (no client secret is used)",
            Location = new Point(20, 72),
            Size = new Size(580, 60),
            AutoSize = false
        });
        var helpLink = new LinkLabel
        {
            Text = "Step-by-step setup guide",
            Location = new Point(20, 134),
            AutoSize = true
        };
        helpLink.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(DeploymentDocUrl) { UseShellExecute = true }); }
            catch { /* best effort */ }
        };
        Controls.Add(helpLink);

        Controls.Add(new Label
        {
            Text = "Tenant ID",
            Location = new Point(20, 170),
            AutoSize = true
        });
        _tenantIdBox = new TextBox
        {
            Location = new Point(20, 190),
            Size = new Size(580, 24),
            PlaceholderText = "00000000-0000-0000-0000-000000000000"
        };
        _tenantIdBox.TextChanged += (_, _) => UpdateValidation();
        Controls.Add(_tenantIdBox);

        Controls.Add(new Label
        {
            Text = "Application (client) ID",
            Location = new Point(20, 228),
            AutoSize = true
        });
        _clientIdBox = new TextBox
        {
            Location = new Point(20, 248),
            Size = new Size(580, 24),
            PlaceholderText = "00000000-0000-0000-0000-000000000000"
        };
        _clientIdBox.TextChanged += (_, _) => UpdateValidation();
        Controls.Add(_clientIdBox);

        Controls.Add(new Label
        {
            Text = "Drives",
            Font = new Font("Segoe UI Semibold", 10F),
            Location = new Point(20, 286),
            AutoSize = true
        });

        _drivesList = new ListView
        {
            Location = new Point(20, 310),
            Size = new Size(480, 140),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = true
        };
        _drivesList.Columns.Add("Letter", 60);
        _drivesList.Columns.Add("Label", 160);
        _drivesList.Columns.Add("Type", 90);
        _drivesList.Columns.Add("Site URL / library", 165);
        _drivesList.SelectedIndexChanged += (_, _) => UpdateDriveButtons();
        _drivesList.DoubleClick += (_, _) => EditSelectedDrive();
        Controls.Add(_drivesList);

        var addDriveButton = new Button
        {
            Text = "Add...",
            Location = new Point(510, 310),
            Size = new Size(90, 30)
        };
        addDriveButton.Click += (_, _) => AddDrive();
        Controls.Add(addDriveButton);

        _editDriveButton = new Button
        {
            Text = "Edit...",
            Location = new Point(510, 346),
            Size = new Size(90, 30)
        };
        _editDriveButton.Click += (_, _) => EditSelectedDrive();
        Controls.Add(_editDriveButton);

        _removeDriveButton = new Button
        {
            Text = "Remove",
            Location = new Point(510, 382),
            Size = new Size(90, 30)
        };
        _removeDriveButton.Click += (_, _) => RemoveSelectedDrive();
        Controls.Add(_removeDriveButton);

        Controls.Add(new Label
        {
            Text = "To add more drives later or change advanced options (folder redirection, sync tuning), " +
                   "edit %LOCALAPPDATA%\\OneSync\\config.json.",
            Location = new Point(20, 460),
            Size = new Size(580, 32),
            ForeColor = Color.DimGray,
            AutoSize = false
        });

        _statusLabel = new Label
        {
            Location = new Point(20, 510),
            Size = new Size(580, 36),
            ForeColor = Color.Firebrick,
            AutoSize = false
        };
        Controls.Add(_statusLabel);

        _saveButton = new Button
        {
            Text = "Save and start OneSync",
            Location = new Point(390, 570),
            Size = new Size(210, 34)
        };
        _saveButton.Click += SaveButton_Click;
        Controls.Add(_saveButton);
        AcceptButton = _saveButton;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(300, 570),
            Size = new Size(80, 34),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancelButton);
        CancelButton = cancelButton;

        // Pre-populate with the most common case so users with only an H: OneDrive
        // can hit Save without opening the Add dialog.
        _drives.Add(new DriveConfig
        {
            Letter = "H",
            Label = "Home Folder",
            Type = "onedrive",
            RemotePath = "/",
            SyncMode = "bidirectional",
            Priority = 1,
            FolderRedirection = new List<string>
            {
                "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos"
            }
        });
        RefreshDrivesList();
        UpdateDriveButtons();
        UpdateValidation();
    }

    private void RefreshDrivesList()
    {
        _drivesList.BeginUpdate();
        _drivesList.Items.Clear();
        foreach (var d in _drives)
        {
            var item = new ListViewItem(d.Letter);
            item.SubItems.Add(d.Label);
            item.SubItems.Add(d.IsSharePoint ? "SharePoint" : "OneDrive");
            item.SubItems.Add(d.IsSharePoint
                ? ($"{d.SiteUrl}  ({d.LibraryName})")
                : string.Empty);
            _drivesList.Items.Add(item);
        }
        _drivesList.EndUpdate();
    }

    private void AddDrive()
    {
        var letters = _drives.Select(d => d.Letter);
        using var dialog = new DriveEditDialog(existing: null, existingLetters: letters, isEdit: false);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // Assign priority based on insertion order so the upload scheduler has
            // a stable preference between drives.
            dialog.Result.Priority = _drives.Count + 1;
            _drives.Add(dialog.Result);
            RefreshDrivesList();
            UpdateValidation();
        }
    }

    private void EditSelectedDrive()
    {
        var idx = _drivesList.SelectedIndices.Count > 0 ? _drivesList.SelectedIndices[0] : -1;
        if (idx < 0 || idx >= _drives.Count) return;
        var existing = _drives[idx];
        var letters = _drives.Select(d => d.Letter);
        using var dialog = new DriveEditDialog(existing, letters, isEdit: true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // Preserve priority on edit.
            dialog.Result.Priority = existing.Priority;
            _drives[idx] = dialog.Result;
            RefreshDrivesList();
            UpdateValidation();
        }
    }

    private void RemoveSelectedDrive()
    {
        var idx = _drivesList.SelectedIndices.Count > 0 ? _drivesList.SelectedIndices[0] : -1;
        if (idx < 0 || idx >= _drives.Count) return;
        _drives.RemoveAt(idx);
        RefreshDrivesList();
        UpdateValidation();
    }

    private void UpdateDriveButtons()
    {
        var hasSelection = _drivesList.SelectedIndices.Count > 0;
        _editDriveButton.Enabled = hasSelection;
        _removeDriveButton.Enabled = hasSelection;
    }

    private void UpdateValidation()
    {
        var tenantValid = TryParseRealGuid(_tenantIdBox.Text);
        var clientValid = TryParseRealGuid(_clientIdBox.Text);
        var drivesValid = _drives.Count > 0;
        _saveButton.Enabled = tenantValid && clientValid && drivesValid;

        if (!string.IsNullOrWhiteSpace(_tenantIdBox.Text) && !tenantValid)
            _statusLabel.Text = "Tenant ID must be a non-empty GUID.";
        else if (!string.IsNullOrWhiteSpace(_clientIdBox.Text) && !clientValid)
            _statusLabel.Text = "Client ID must be a non-empty GUID.";
        else if (!drivesValid)
            _statusLabel.Text = "Add at least one drive.";
        else
            _statusLabel.Text = string.Empty;
    }

    private static bool TryParseRealGuid(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Guid.TryParse(text.Trim(), out var g) && g != Guid.Empty;
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        try
        {
            var tenantId = _tenantIdBox.Text.Trim();
            var clientId = _clientIdBox.Text.Trim();

            if (!File.Exists(_templatePath))
                throw new FileNotFoundException(
                    $"Template config.json not found at {_templatePath}");

            // Parse the shipped placeholder config so we inherit every default
            // (cleanup, syncSettings, logging, etc.) the user might not yet
            // care about. We're going to overwrite the auth + drives fields.
            var templateJson = File.ReadAllText(_templatePath);
            var config = JsonSerializer.Deserialize<AppConfig>(templateJson, ReadOptions)
                ?? new AppConfig();

            config.TenantId = tenantId;
            config.ClientId = clientId;
            config.Authority = $"https://login.microsoftonline.com/{tenantId}";
            config.Drives = _drives;

            var newJson = JsonSerializer.Serialize(config, WriteOptions);

            var userConfigDir = Environment.ExpandEnvironmentVariables(
                @"%LOCALAPPDATA%\OneSync");
            Directory.CreateDirectory(userConfigDir);
            var userConfigPath = Path.Combine(userConfigDir, "config.json");
            File.WriteAllText(userConfigPath, newJson);

            WrittenConfigPath = userConfigPath;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Could not save: " + ex.Message;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
