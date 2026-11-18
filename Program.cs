using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;

namespace WiFiAutoControl;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

internal sealed class Settings
{
    public int IdleMinutes { get; set; } = 15;
    public string AdapterName { get; set; } = "Wi-Fi";
    public bool MonitoringEnabled { get; set; } = true;
    public bool DisabledByThisApp { get; set; } = false;
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer;
    private readonly string settingsPath;
    private Settings settings;
    private bool busy;

    public TrayAppContext()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WiFiAutoControl");
        Directory.CreateDirectory(dir);
        settingsPath = Path.Combine(dir, "settings.json");
        settings = LoadSettings();

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WiFi AutoControl — iniciando",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        tray.DoubleClick += (_, _) => ShowStatus();

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();

        // Se o programa anterior caiu enquanto havia desligado o adaptador,
        // tenta restaurar o Wi-Fi no próximo início.
        if (settings.DisabledByThisApp)
            _ = RestoreAfterUnexpectedExitAsync();
        UpdateTrayText();
    }

    private async Task RestoreAfterUnexpectedExitAsync()
    {
        busy = true; // impede que o TickAsync tente religar o adaptador ao mesmo tempo
        try
        {
            // Só zera o marcador se o Enable-NetAdapter confirmar sucesso. Se falhar, ele
            // continua true: o TickAsync tenta de novo ao detectar atividade, e a próxima
            // inicialização também.
            if (await SetAdapterEnabled(true))
            {
                settings.DisabledByThisApp = false;
                SaveSettings();
            }
        }
        finally { busy = false; }
        UpdateTrayText();
    }

    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    private void SaveSettings()
    {
        try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem("Wi-Fi AutoControl") { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());

        var monitor = new ToolStripMenuItem("Monitoramento automático") { CheckOnClick = true, Checked = settings.MonitoringEnabled };
        monitor.CheckedChanged += async (_, _) =>
        {
            settings.MonitoringEnabled = monitor.Checked;
            if (!monitor.Checked && settings.DisabledByThisApp)
            {
                bool ok = await SetAdapterEnabled(true);
                if (ok) settings.DisabledByThisApp = false;
            }
            SaveSettings(); UpdateTrayText();
        };
        menu.Items.Add(monitor);

        var interval = new ToolStripMenuItem($"Desligar após: {settings.IdleMinutes} minutos");
        foreach (int mins in new[] { 5, 10, 15, 20, 30, 45, 60 })
        {
            var item = new ToolStripMenuItem($"{mins} minutos") { Checked = settings.IdleMinutes == mins };
            item.Click += (_, _) => { settings.IdleMinutes = mins; SaveSettings(); RebuildMenu(); };
            interval.DropDownItems.Add(item);
        }
        menu.Items.Add(interval);

        var adapter = new ToolStripMenuItem($"Adaptador: {settings.AdapterName}");
        foreach (string name in GetAdapterNames())
        {
            var item = new ToolStripMenuItem(name) { Checked = string.Equals(name, settings.AdapterName, StringComparison.OrdinalIgnoreCase) };
            item.Click += async (_, _) =>
            {
                if (settings.DisabledByThisApp)
                {
                    // Religa o adaptador atual antes de trocar. Se falhar, não troca: o
                    // marcador DisabledByThisApp precisa continuar apontando para o
                    // adaptador que ficou desligado, para ser religado depois.
                    if (!await SetAdapterEnabled(true))
                    {
                        MessageBox.Show($"Não foi possível religar o adaptador \"{settings.AdapterName}\". O adaptador selecionado não foi alterado.", "WiFi AutoControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    settings.DisabledByThisApp = false;
                }
                settings.AdapterName = name;
                SaveSettings();
                RebuildMenu();
            };
            adapter.DropDownItems.Add(item);
        }
        if (adapter.DropDownItems.Count == 0)
        {
            var none = new ToolStripMenuItem("Não foi possível listar adaptadores") { Enabled = false };
            adapter.DropDownItems.Add(none);
        }
        menu.Items.Add(adapter);

        menu.Items.Add(new ToolStripSeparator());
        var statusNow = new ToolStripMenuItem("Estado: verificando…") { Enabled = false };
        menu.Items.Add(statusNow);
        // Consulta o estado real a cada abertura do menu, em segundo plano, para não
        // atrasar a exibição do menu enquanto o PowerShell responde.
        menu.Opening += async (_, _) =>
        {
            statusNow.Text = "Estado: verificando…";
            string text = await Task.Run(() => GetAdapterStatus());
            if (!statusNow.IsDisposed) statusNow.Text = text;
        };
        var pause = new ToolStripMenuItem("Reativar Wi-Fi agora");
        pause.Click += async (_, _) =>
        {
            // Só zera o marcador se o Enable-NetAdapter confirmar sucesso.
            if (await SetAdapterEnabled(true))
            {
                settings.DisabledByThisApp = false;
                SaveSettings();
            }
            else
            {
                MessageBox.Show($"Não foi possível religar o adaptador \"{settings.AdapterName}\".", "WiFi AutoControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateTrayText();
        };
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Sair");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);
        menu.Closed += (_, _) => { };
        return menu;
    }

    private void RebuildMenu()
    {
        var old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private async Task TickAsync()
    {
        if (busy || !settings.MonitoringEnabled) { UpdateTrayText(); return; }
        uint idle = IdleTime.GetMilliseconds();
        uint limit = (uint)settings.IdleMinutes * 60_000;

        if (idle >= limit && !settings.DisabledByThisApp)
        {
            busy = true;
            bool ok = await SetAdapterEnabled(false);
            if (ok) { settings.DisabledByThisApp = true; SaveSettings(); }
            busy = false;
        }
        else if (idle < 5000 && settings.DisabledByThisApp)
        {
            busy = true;
            bool ok = await SetAdapterEnabled(true);
            if (ok) { settings.DisabledByThisApp = false; SaveSettings(); }
            busy = false;
        }
        UpdateTrayText();
    }

    private async Task<bool> SetAdapterEnabled(bool enabled, int timeoutMs = 30_000)
    {
        string cmd = enabled
            ? $"Enable-NetAdapter -Name {PsQuote(settings.AdapterName)} -Confirm:$false -ErrorAction Stop"
            : $"Disable-NetAdapter -Name {PsQuote(settings.AdapterName)} -Confirm:$false -ErrorAction Stop";
        try
        {
            // stdout/stderr não são redirecionados: ninguém os lê, e um pipe cheio
            // deixaria o PowerShell bloqueado esperando espaço para escrever.
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "`\"") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                // ConfigureAwait(false): a continuação não precisa da thread da UI. Sem isso,
                // ExitThreadCore (que bloqueia a UI com GetResult()) entraria em deadlock.
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // O PowerShell não respondeu a tempo: encerra e informa falha, para que o
                // chamador não fique esperando para sempre nem zere DisabledByThisApp.
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private List<string> GetAdapterNames()
    {
        try
        {
            string output = RunPowerShell("Get-NetAdapter -Physical | Select-Object -ExpandProperty Name");
            var names = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!names.Contains(settings.AdapterName, StringComparer.OrdinalIgnoreCase) && names.Any(x => x.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase)))
                settings.AdapterName = "Wi-Fi";
            else if (!names.Contains(settings.AdapterName, StringComparer.OrdinalIgnoreCase))
            {
                string? likely = names.FirstOrDefault(x => x.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) || x.Contains("wireless", StringComparison.OrdinalIgnoreCase));
                if (likely != null) settings.AdapterName = likely;
            }
            SaveSettings();
            return names;
        }
        catch { return new List<string>(); }
    }

    private string GetAdapterStatus()
    {
        try
        {
            string output = RunPowerShell($"(Get-NetAdapter -Name {PsQuote(settings.AdapterName)} -ErrorAction Stop).Status");
            return "Estado: " + (output.Trim() switch { "Up" => "Wi-Fi ligado", "Disabled" => "Wi-Fi desligado", "Disconnected" => "Wi-Fi ligado (sem conexão)", "" => "estado indisponível", var s => s });
        }
        catch { return "Estado do Wi-Fi indisponível"; }
    }

    private static string RunPowerShell(string command, int timeoutMs = 5000)
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "`\"") + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new Exception("Não foi possível iniciar o PowerShell.");
        // Lê stdout e stderr em segundo plano. Um ReadToEnd() síncrono só retorna quando o
        // processo fecha a saída, ou seja, o timeout de WaitForExit nunca chegava a valer;
        // e um stderr redirecionado sem leitura pode encher o pipe e travar o PowerShell.
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"O PowerShell não respondeu em {timeoutMs / 1000} s.");
        }
        // O processo já terminou; os pipes fecham em seguida. O limite extra evita ficar
        // preso caso algum processo filho tenha herdado o pipe e o mantenha aberto.
        if (!Task.WaitAll(new Task[] { stdout, stderr }, 1000))
            throw new TimeoutException("A saída do PowerShell não foi concluída.");
        return stdout.Result;
    }

    private void UpdateTrayText()
    {
        try
        {
            string state = settings.DisabledByThisApp ? "Wi-Fi desligado" : "Wi-Fi ligado";
            string mode = settings.MonitoringEnabled ? $"automático: {settings.IdleMinutes} min" : "monitoramento pausado";
            tray.Text = $"WiFi AutoControl — {state}; {mode}";
        }
        catch { }
    }

    private void ShowStatus()
    {
        MessageBox.Show($"Adaptador: {settings.AdapterName}\nDesligamento por inatividade: {settings.IdleMinutes} minutos\nMonitoramento: {(settings.MonitoringEnabled ? "Ativo" : "Pausado")}\nWi-Fi desligado pelo programa: {(settings.DisabledByThisApp ? "Sim" : "Não")}\n\nO Wi-Fi volta quando houver atividade de teclado ou mouse.", "WiFi AutoControl", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        if (settings.DisabledByThisApp)
        {
            // Bloquear aqui é seguro porque SetAdapterEnabled usa ConfigureAwait(false)
            // e não depende da thread da UI (que está parada neste GetResult()).
            // O limite de 10 s evita que o "Sair" fique travado se o PowerShell não responder.
            bool ok = false;
            try { ok = SetAdapterEnabled(true, 10_000).GetAwaiter().GetResult(); } catch { }
            if (ok)
            {
                settings.DisabledByThisApp = false;
            }
            else
            {
                // Mantém o marcador: a próxima inicialização tenta religar o adaptador.
                MessageBox.Show($"Não foi possível religar o adaptador \"{settings.AdapterName}\". O programa tentará novamente na próxima vez que for iniciado.", "WiFi AutoControl", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        SaveSettings();
        tray.Visible = false;
        tray.Dispose();
        timer.Dispose();
        base.ExitThreadCore();
    }
}

internal static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static uint GetMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }
}
