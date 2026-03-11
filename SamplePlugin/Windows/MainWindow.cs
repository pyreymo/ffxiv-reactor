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

namespace SamplePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly List<ClassJob> availableJobs = [];
    private readonly Dictionary<uint, List<Action>> actionsByJob = [];
    private uint selectedJobId = 0;

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

        // 获取所有有效的职业
        availableJobs.AddRange(jobSheet.Where(j => !string.IsNullOrEmpty(j.Abbreviation.ToString())));

        // 遍历技能并按职业分组
        foreach (var action in actionSheet.Where(a => a.IsPlayerAction && a.Icon != 0))
        {
            var jobId = action.ClassJob.RowId;
            if (!actionsByJob.TryGetValue(jobId, out var value))
            {
                value = [];
                actionsByJob[jobId] = value;
            }

            value.Add(action);
        }

        // 默认选中第一个职业
        if (availableJobs.Count != 0)
        {
            selectedJobId = availableJobs.First().RowId;
        }
    }

    public override void Draw()
    {
        // 创建一个2列的表格用于左右分栏
        using var splitTable = ImRaii.Table("SplitLayout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        if (!splitTable.Success) return;

        // 设置左列固定宽度，右列自动拉伸
        ImGui.TableSetupColumn("LeftJobColumn", ImGuiTableColumnFlags.WidthFixed, 200f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("RightActionColumn", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        // ================= 左侧：职业列表 =================
        ImGui.TableNextColumn();
        using (var leftChild = ImRaii.Child("JobListChild", Vector2.Zero, false))
        {
            if (leftChild.Success)
            {
                DrawJobList();
            }
        }

        // ================= 右侧：技能矩阵 =================
        ImGui.TableNextColumn();
        using (var rightChild = ImRaii.Child("ActionGridChild", Vector2.Zero, false))
        {
            if (rightChild.Success)
            {
                DrawActionGrid();
            }
        }
    }

    private void DrawJobList()
    {
        foreach (var job in availableJobs)
        {
            var jobName = job.Name.ToString();
            if (string.IsNullOrEmpty(jobName)) continue;

            // 判断当前行是否被选中
            var isSelected = selectedJobId == job.RowId;
            if (ImGui.Selectable($"{jobName}##job_{job.RowId}", isSelected))
            {
                selectedJobId = job.RowId;
            }
        }
    }

    private void DrawActionGrid()
    {
        if (!actionsByJob.TryGetValue(selectedJobId, out var actions) || actions.Count == 0)
        {
            ImGui.Text("当前职业没有找到技能，或包含复杂绑定逻辑需进一步过滤。");
            return;
        }

        // 动态计算列数
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var itemWidth = 180f * ImGuiHelpers.GlobalScale;
        var columns = Math.Max(1, (int)(availableWidth / itemWidth));

        // 使用表格实现 NxN 矩阵布局
        using var gridTable = ImRaii.Table("ActionGrid", columns);
        if (!gridTable.Success) return;

        foreach (var action in actions)
        {
            ImGui.TableNextColumn();

            // 使用 Group 将图标和文字打包在一起，避免排版混乱
            using (ImRaii.Group())
            {
                // 获取技能图标
                var iconLookup = new GameIconLookup(action.Icon);
                var iconTex = Plugin.TextureProvider.GetFromGameIcon(iconLookup).GetWrapOrDefault();

                if (iconTex != null)
                {
                    ImGui.Image(iconTex.Handle, new Vector2(40, 40) * ImGuiHelpers.GlobalScale);
                    ImGui.SameLine();
                }

                // 右侧显示技能名和ID
                using (ImRaii.Group())
                {
                    ImGui.TextUnformatted(action.Name.ToString());
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), $"ID: {action.RowId}");
                }
            }
        }
    }
}
