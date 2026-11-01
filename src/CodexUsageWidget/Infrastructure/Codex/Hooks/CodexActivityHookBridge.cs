namespace CodexUsageWidget.Infrastructure.Codex.Hooks;

public static class CodexActivityHookBridge
{
    private const int PipeConnectTimeoutMilliseconds = 1000;

    public const int HookTimeoutSeconds = 3;

    public static string Command => BuildCommand(CodexActivityPipeClient.DefaultPipeName);

    public static string LegacyCommand => BuildCommandCore(CodexActivityPipeClient.DefaultPipeName, reliable: false);

    public static string BuildCommand(string pipeName, string? stateDirectory = null)
        => BuildCommandCore(pipeName, reliable: true, stateDirectory);

    private static string BuildCommandCore(string pipeName, bool reliable, string? stateDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var escapedPipeName = pipeName.Replace("'", "''", StringComparison.Ordinal);

        var statePathExpression = stateDirectory is null
            ? "[IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'),'CodexUsageWidget','activity-state')"
            : "'" + stateDirectory.Replace("'", "''", StringComparison.Ordinal) + "'";
        var persist = reliable && (pipeName == CodexActivityPipeClient.DefaultPipeName || stateDirectory is not null)
            ? "$q=" + statePathExpression + ";" +
              "try{if($s-match'^[a-zA-Z0-9_-]{1,128}$'){[IO.Directory]::CreateDirectory($q)|Out-Null;" +
              "$f=[IO.Path]::Combine($q,$s+'.json');$x=[Threading.Mutex]::new($false,('Local\\CodexUsageWidget.ActivityState.'+$s));" +
              "try{if($x.WaitOne(200)){try{$o=$null;if([IO.File]::Exists($f)){$o=[IO.File]::ReadAllText($f)|ConvertFrom-Json};" +
              "if(($null-eq$o-or$k-ne1-or$o.TurnId-eq$t)-and($null-eq$o-or$o.TimestampTicks-lt$n)){[IO.File]::WriteAllText($f,$m)}}finally{$x.ReleaseMutex()}}}finally{$x.Dispose()}}}catch{};"
            : string.Empty;
        var connect = reliable
            ? "for($a=0;$a-lt3;$a++){try{$p.Connect(250);break}catch{if($a-eq2){throw};Start-Sleep -Milliseconds 50}};"
            : $"$p.Connect({PipeConnectTimeoutMilliseconds});";
        return "$ErrorActionPreference='Stop';try{" +
               "$h=[Console]::In.ReadToEnd()|ConvertFrom-Json;" +
               (reliable
                   ? "$k=@{'UserPromptSubmit'=0;'Stop'=1;'Interrupt'=1;'SessionEnd'=2}[[string]$h.hook_event_name];"
                   : "$k=@{'UserPromptSubmit'=0;'Stop'=1;'SessionEnd'=2}[[string]$h.hook_event_name];") +
               "$s=[string]$h.session_id;$t=[string]$h.turn_id;" +
               "if($null-ne$k-and-not[string]::IsNullOrWhiteSpace($s)-and" +
               "($k-eq2-or-not[string]::IsNullOrWhiteSpace($t))){" +
               "$u=$null;if($k-ne2){$u=$t};" +
               (reliable ? "$n=[DateTime]::UtcNow.Ticks;" : string.Empty) +
               "$m=[ordered]@{Kind=$k;SessionId=$s;TurnId=$u" + (reliable ? ";TimestampTicks=$n" : string.Empty) + "}|ConvertTo-Json -Compress;" +
               persist +
               "$b=[Text.Encoding]::UTF8.GetBytes($m);" +
               "$p=[IO.Pipes.NamedPipeClientStream]::new('.'," +
               $"'{escapedPipeName}',[IO.Pipes.PipeDirection]::Out," +
               "[IO.Pipes.PipeOptions]::Asynchronous);" +
               "try{" +
               connect +
               "$l=[BitConverter]::GetBytes([int]$b.Length);" +
               "$p.Write($l,0,$l.Length);$p.Write($b,0,$b.Length);$p.Flush()" +
               "}finally{$p.Dispose()}" +
               "}}catch{" + (reliable ? "[Console]::Error.WriteLine('Codex activity delivery failed; local state will be retried by the widget.')" : string.Empty) +
               "};[Console]::Out.WriteLine('{\"continue\":true}')";
    }
}
