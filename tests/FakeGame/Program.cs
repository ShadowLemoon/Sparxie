// A dummy game for the end-to-end tests: records its own command line into the file named by FAKE_GAME_RECORD, then lives FAKE_GAME_LIFE_MS ms.
var record = Environment.GetEnvironmentVariable("FAKE_GAME_RECORD");
if (!string.IsNullOrEmpty(record))
{
    var temp = record + ".tmp";
    File.WriteAllText(temp, "cmdline=" + Environment.CommandLine + "\npid=" + Environment.ProcessId + "\n");
    File.Move(temp, record, overwrite: true);
}

var life = int.TryParse(Environment.GetEnvironmentVariable("FAKE_GAME_LIFE_MS"), out var ms) && ms > 0 ? ms : 120_000;
Thread.Sleep(life);
