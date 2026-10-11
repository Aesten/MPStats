using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MPStats;

public class MPStatsSubModule : MBSubModuleBase
{
    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        Log("Loaded.");
        StatsCommand.RouteThroughMPManager(); // so !help lists !stats from the start, not from the first mission
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        if (GameNetwork.IsDedicatedServer)
        {
            StatsCommand.ProcessPending();
        }
    }

    public override void OnMissionBehaviorInitialize(Mission mission)
    {
        base.OnMissionBehaviorInitialize(mission);
        if (!GameNetwork.IsDedicatedServer)
        {
            return;
        }
        StatsCommand.Attach();
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
