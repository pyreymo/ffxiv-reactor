using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace FFReactor.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // 左侧职业列表与分组缓存
    private readonly List<ClassJob> availableJobs = [];
    private readonly Dictionary<uint, List<Action>> actionsByJob = [];
    private uint selectedJobId = 0;

    // 搜索相关缓存
    private readonly List<Action> allValidActions = []; // 包含所有可供搜索的技能
    private List<Action> searchResults = [];
    private string searchText = string.Empty;

    public MainWindow(Plugin plugin)
        : base("技能选择器##MainWindow", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;

        InitializeData();
    }

    public void Dispose() { }

    private void InitializeData()
    {
        var jobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        var actionSheet = Plugin.DataManager.GetExcelSheet<Action>();

        if (jobSheet == null || actionSheet == null) return;

        // 1. 获取所有有效的职业
        availableJobs.AddRange(jobSheet.Where(j => !string.IsNullOrEmpty(j.Abbreviation.ToString())));

        // 2. 遍历技能表
        // 过滤出有图标、有名称的技能作为“总技能池”，涵盖坐骑技能、任务技能、通用技能等
        foreach (var action in actionSheet.Where(a => a.Icon != 0 && !string.IsNullOrEmpty(a.Name.ToString())))
        {
            allValidActions.Add(action);

            // 只有属于玩家的常规技能，才会被归类到左侧的职业字典中
            if (action.IsPlayerAction)
            {
                var jobId = action.ClassJob.RowId;
                if (!actionsByJob.TryGetValue(jobId, out var value))
                {
                    value = [];
                    actionsByJob[jobId] = value;
                }
                value.Add(action);
            }
        }

        // 默认选中第一个职业
        if (availableJobs.Count != 0)
        {
            selectedJobId = availableJobs.First().RowId;
        }
    }

    public override void Draw()
    {
        // ================= 顶部：搜索栏 =================
        DrawSearchBar();

        ImGui.Spacing();

        // ================= 下方：左右分栏 =================
        using var splitTable = ImRaii.Table("SplitLayout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        if (!splitTable.Success) return;

        ImGui.TableSetupColumn("LeftJobColumn", ImGuiTableColumnFlags.WidthFixed, 220f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("RightActionColumn", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        // --- 左侧：职业列表 ---
        ImGui.TableNextColumn();
        using (var leftChild = ImRaii.Child("JobListChild", Vector2.Zero, false))
        {
            if (leftChild.Success)
            {
                // 如果正在搜索，可以将被遮挡感觉的左侧列表稍微变暗（可选）
                var isSearching = !string.IsNullOrWhiteSpace(searchText);
                if (isSearching) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);

                DrawJobList();

                if (isSearching) ImGui.PopStyleVar();
            }
        }

        // --- 右侧：技能矩阵 ---
        ImGui.TableNextColumn();
        using (var rightChild = ImRaii.Child("ActionGridChild", Vector2.Zero, false))
        {
            if (rightChild.Success)
            {
                DrawActionGrid();
            }
        }
    }

    private void DrawSearchBar()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        // InputText 只有在内容发生变化时才会返回 true
        if (ImGui.InputTextWithHint("##searchBox", "搜索技能名称 (支持拼音或汉字) 或 技能 ID...", ref searchText, 100))
        {
            UpdateSearchResults();
        }
    }

    private void UpdateSearchResults()
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            searchResults.Clear();
            return;
        }

        var query = searchText.ToLowerInvariant();
        var isIdQuery = uint.TryParse(query, out var queryId);

        // 过滤总技能池，匹配 ID 或 名称
        searchResults = [.. allValidActions.Where(a =>
            (isIdQuery && a.RowId == queryId) ||
            a.Name.ToString().Contains(query, StringComparison.InvariantCultureIgnoreCase)
        )];
    }

    private void DrawJobList()
    {
        foreach (var job in availableJobs)
        {
            var jobName = job.Name.ToString();
            if (string.IsNullOrEmpty(jobName)) continue;

            var isSelected = selectedJobId == job.RowId && string.IsNullOrWhiteSpace(searchText);

            var rowHeight = 28f * ImGuiHelpers.GlobalScale;
            var startPos = ImGui.GetCursorPos();

            if (ImGui.Selectable($"##job_sel_{job.RowId}", isSelected, ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, rowHeight)))
            {
                selectedJobId = job.RowId;
                // 点击左侧职业时，自动清空搜索框以展示该职业技能
                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    searchText = string.Empty;
                    searchResults.Clear();
                }
            }

            ImGui.SetCursorPos(new Vector2(startPos.X + 4f, startPos.Y + (2f * ImGuiHelpers.GlobalScale)));

            var jobIconId = (uint)(62100 + job.RowId);
            var iconTex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrDefault();

            if (iconTex != null)
            {
                ImGui.Image(iconTex.Handle, new Vector2(24, 24) * ImGuiHelpers.GlobalScale);
                ImGui.SameLine();
            }

            ImGui.SetCursorPosY(startPos.Y + (5f * ImGuiHelpers.GlobalScale));
            ImGui.TextUnformatted(jobName);

            ImGui.SetCursorPos(new Vector2(startPos.X, startPos.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y));
        }
    }

    private void DrawActionGrid()
    {
        // 决定要显示的数据源：如果搜索框有内容，显示搜索结果；否则显示左侧选中的职业技能
        List<Action>? actionsToDisplay;
        var isSearching = !string.IsNullOrWhiteSpace(searchText);

        if (isSearching)
        {
            actionsToDisplay = searchResults;
            if (actionsToDisplay.Count == 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "未找到匹配的技能...");
                return;
            }
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"找到 {actionsToDisplay.Count} 个匹配项：");
            ImGui.Separator();
        }
        else
        {
            if (!actionsByJob.TryGetValue(selectedJobId, out actionsToDisplay) || actionsToDisplay.Count == 0)
            {
                ImGui.Text("当前职业没有找到技能，或包含复杂绑定逻辑需进一步过滤。");
                return;
            }
        }

        // 动态计算列数
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var itemWidth = 180f * ImGuiHelpers.GlobalScale;
        var columns = Math.Max(1, (int)(availableWidth / itemWidth));

        // 使用表格实现 NxN 矩阵布局
        using var gridTable = ImRaii.Table("ActionGrid", columns);
        if (!gridTable.Success) return;

        foreach (var action in actionsToDisplay)
        {
            ImGui.TableNextColumn();

            var startPos = ImGui.GetCursorPos();
            var itemHeight = 48f * ImGuiHelpers.GlobalScale;

            if (ImGui.Selectable($"##act_sel_{action.RowId}", false, ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, itemHeight)))
            {
                plugin.Log.Info($"点击了技能: {action.Name} (ID: {action.RowId})");
            }

            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                if (tooltip.Success)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), action.Name.ToString());
                    ImGui.Separator();

                    ImGui.TextUnformatted($"技能 ID: {action.RowId}");
                    ImGui.TextUnformatted($"技能类型: {action.ActionCategory.Value.Name}");

                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "[ 点击配置技能喊话 ]");
                }
            }

            ImGui.SetCursorPos(new Vector2(startPos.X + (4f * ImGuiHelpers.GlobalScale), startPos.Y + (4f * ImGuiHelpers.GlobalScale)));

            using (ImRaii.Group())
            {
                var iconLookup = new GameIconLookup(action.Icon);
                var iconTex = Plugin.TextureProvider.GetFromGameIcon(iconLookup).GetWrapOrDefault();

                if (iconTex != null)
                {
                    ImGui.Image(iconTex.Handle, new Vector2(40, 40) * ImGuiHelpers.GlobalScale);
                    ImGui.SameLine();
                }

                using (ImRaii.Group())
                {
                    ImGui.TextUnformatted(action.Name.ToString());
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), $"ID: {action.RowId}");
                }
            }

            ImGui.SetCursorPos(new Vector2(startPos.X, startPos.Y + itemHeight + ImGui.GetStyle().ItemSpacing.Y));
        }
    }
}
