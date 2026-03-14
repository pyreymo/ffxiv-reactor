using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FFReactor;

public unsafe class ReviveTracker : IDisposable
{
    private readonly HashSet<uint> reviveSpellIds = [125, 173, 3603, 24287];
    private const uint RaiseStatusId = 148;

    private delegate bool UseActionDelegate(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint param4, uint param5, uint param6, void* param7);
    private readonly Hook<UseActionDelegate>? useActionHook;

    private ulong? pendingTargetId = null;
    private long statusCheckStartTime = 0;

    public ReviveTracker()
    {
        var useActionAddress = (nint)ActionManager.MemberFunctionPointers.UseAction;
        useActionHook = Plugin.HookProvider.HookFromAddress<UseActionDelegate>(useActionAddress, UseActionDetour);
        useActionHook.Enable();

        Plugin.Framework.Update += OnUpdate;
    }

    private bool UseActionDetour(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint param4, uint param5, uint param6, void* param7)
    {
        var actionSuccessful = useActionHook!.Original(actionManager, actionType, actionID, targetID, param4, param5, param6, param7);

        if (actionSuccessful && actionType == ActionType.Action && reviveSpellIds.Contains(actionID))
        {
            pendingTargetId = targetID;
            statusCheckStartTime = Environment.TickCount64;
        }

        return actionSuccessful;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!pendingTargetId.HasValue) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        if (player.IsCasting && reviveSpellIds.Contains(player.CastActionId))
        {
            statusCheckStartTime = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - statusCheckStartTime > 3000)
        {
            pendingTargetId = null;
            return;
        }

        var target = Plugin.ObjectTable.SearchById((uint)pendingTargetId.Value);

        if (target is IBattleChara battleChara)
        {
            foreach (var status in battleChara.StatusList)
            {
                if (status.StatusId == RaiseStatusId && status.SourceId == player.GameObjectId)
                {
                    Chat.SendMessage($"/p 【{target.Name}】抢救成功");

                    pendingTargetId = null;
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;

        useActionHook?.Disable();
        useActionHook?.Dispose();
    }
}
