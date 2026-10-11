using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPStats;

public class MPStatsSubModule : MBSubModuleBase
{
    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Log("Loaded.");
    }

    public override void OnMissionBehaviorInitialize(Mission mission)
    {
        base.OnMissionBehaviorInitialize(mission);
        if (!GameNetwork.IsDedicatedServer)
        {
            return;
        }
        string gameType = MultiplayerOptions.OptionType.GameType.GetStrValue();
        if (gameType == "Skirmish" || gameType == "Battle")
        {
            mission.AddMissionBehavior(new MatchRecorder());
        }
        if (gameType == "Battle")
        {
            mission.AddMissionBehavior(new GroupfightRoundStats());
        }
    }

    internal static void Log(string message)
    {
        Debug.Print("[MPStats] " + message, 0, Debug.DebugColor.Green);
    }
}
