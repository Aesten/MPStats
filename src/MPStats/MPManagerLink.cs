// Optional link from a server mod to MPManager, for routing the mod's chat commands through it. Copied from
// mpManager/docs/MPManagerLink.cs (keep in sync); the mod needs no reference to MPManager and still works without it.
//
// With MPManager on the server, the mod's commands get MPManager's handling: hidden from chat,
// command aliases (aliases.txt), the admin check, and a line in !help. Without it, the mod keeps using its
// own chat hook.
//
// Usage, when the mod would attach its own chat hook (e.g. once per mission):
//
//   if (MPManagerLink.TryRegister(new[] { "mpb", "b" }, "<setting> [value]", "Show or change settings.",
//       adminOnly: true, Run))
//   {
//       return; // routed: don't hook chat (or unhook it), or commands would run twice
//   }
//   ... attach the mod's own chat hook ...
//
//   static void Run(NetworkCommunicator peer, string[] args, Action<string> reply)
//   // args: the words after the command name; peer: null when run from the server console (mpm cmd);
//   // reply: answers the caller privately. Runs on the main thread, outside packet handling.
//
// The contract (MPManager's ModCommands.Register) is documented in mpManager's README, "Commands from other mods".

using System;
using System.Linq;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace MPStats;

internal static class MPManagerLink
{
    // True once MPManager routes this mod's commands.
    public static bool Routed { get; private set; }

    // Registers the command with MPManager if it's loaded; false if it isn't (or its contract changed), in which
    // case the mod handles chat itself.
    public static bool TryRegister(string[] names, string usage, string description, bool adminOnly,
        Action<NetworkCommunicator, string[], Action<string>> run)
    {
        if (Routed)
        {
            return true;
        }
        try
        {
            MethodInfo register = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "MPManager")
                .Select(a => a.GetType("MPManager.ModCommands"))
                .FirstOrDefault(t => t != null)
                ?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            if (register == null)
            {
                return false;
            }
            register.Invoke(null, new object[] { names, usage, description, adminOnly, run });
            Routed = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
