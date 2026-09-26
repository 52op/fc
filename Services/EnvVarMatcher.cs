using System;
using System.Collections.Generic;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 规则匹配 + "迁移单元"归并。
    /// 扫描得到的是树节点（可能是叶子）。一个规则往往只匹配到某个祖先目录（如
    /// "%USERPROFILE%\.nuget\packages"），所以右键某个子目录时，需要向上回溯文件系统路径，
    /// 找到第一个能被规则匹配的祖先，作为真正的"迁移单元"。
    /// </summary>
    public static class EnvVarMatcher
    {
        /// <summary>
        /// 从给定目录路径向上回溯，返回第一个匹配到的规则（含匹配到的祖先路径）。
        /// 找不到返回 null。仅向上到指定停止层（默认到盘根）。
        /// </summary>
        public static EnvVarMatch FindMatch(string fullPath, string stopAtRoot = null)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return null;
            }
            string current = PathUtil.TrimSlash(fullPath);
            string stop = string.IsNullOrEmpty(stopAtRoot) ? null : PathUtil.TrimSlash(stopAtRoot);

            while (!string.IsNullOrEmpty(current))
            {
                var rule = EnvVarRuleSet.Match(current);
                if (rule != null)
                {
                    return new EnvVarMatch(rule, current);
                }
                if (stop != null && string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                string parent = System.IO.Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
            return null;
        }

        /// <summary>
        /// 从树节点向上归并：优先用 DiskNode 的 ParentNode 链回溯（已经知道树结构时更准确），
        /// 兜底用文件系统路径回溯。返回第一个命中的规则与对应的 DiskNode 祖先。
        /// </summary>
        public static EnvVarMatch FindMatchOnNode(DiskNode node)
        {
            if (node == null)
            {
                return null;
            }
            // 1) 沿树父链回溯
            var cur = node;
            while (cur != null)
            {
                var rule = EnvVarRuleSet.Match(cur.FullPath);
                if (rule != null)
                {
                    return new EnvVarMatch(rule, cur.FullPath);
                }
                cur = cur.ParentNode;
            }
            // 2) 兜底：文件系统路径回溯
            return FindMatch(node.FullPath);
        }

        /// <summary>
        /// 生成行内标注文本（EnvTagText）。
        /// - EnvVar 类：显示"环境变量：VAR 名"（Community 类加"（社区验证）"）
        /// - JunctionOnly 类：显示"可用联接迁移"
        /// - Cleanable 类：显示软件名 + 标签（如"微信 · 缓存"）
        /// 无匹配返回 null 或空串。
        /// </summary>
        public static string BuildTagText(EnvVarRule rule)
        {
            if (rule == null)
            {
                return null;
            }
            switch (rule.Kind)
            {
                case RedirectKind.EnvVar:
                    string envg = string.IsNullOrEmpty(rule.EnvVarName) ? "" : "环境变量：" + rule.EnvVarName;
                    if (rule.Confidence == RuleConfidence.Community)
                    {
                        envg += "（社区验证）";
                    }
                    return envg;
                case RedirectKind.JunctionOnly:
                    return "可用联接迁移";
                default:
                    string tag = string.IsNullOrEmpty(rule.Tag) ? "" : " · " + rule.Tag;
                    return rule.SoftwareName + tag;
            }
        }

        /// <summary>标注的 tooltip 文本（软件名 + 依据链接）。</summary>
        public static string BuildTooltip(EnvVarRule rule)
        {
            if (rule == null)
            {
                return null;
            }
            string text = rule.SoftwareName;
            if (!string.IsNullOrEmpty(rule.Tag))
            {
                text += "（" + rule.Tag + "）";
            }
            switch (rule.Kind)
            {
                case RedirectKind.EnvVar:
                    text += "\n支持通过环境变量重定位";
                    text += rule.Confidence == RuleConfidence.Official ? "（官方文档确认）" : "（官方源码支持，未文档化）";
                    if (rule.RecommendMove)
                    {
                        text += "\n建议迁移到其他盘";
                    }
                    break;
                case RedirectKind.JunctionOnly:
                    text += "\n不支持环境变量，可用联接（junction）迁移";
                    break;
                default:
                    text += "\n建议清理或按需迁移（非整体迁移项）";
                    break;
            }
            if (!string.IsNullOrEmpty(rule.SourceUrl))
            {
                text += "\n依据：" + rule.SourceUrl;
            }
            return text;
        }
    }

    /// <summary>匹配结果：规则 + 匹配到的祖先目录路径（即"迁移单元"）。</summary>
    public class EnvVarMatch
    {
        public EnvVarRule Rule { get; private set; }

        public string MatchedPath { get; private set; }

        public EnvVarMatch(EnvVarRule rule, string matchedPath)
        {
            Rule = rule;
            MatchedPath = matchedPath;
        }
    }
}