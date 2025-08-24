using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Nuuz.Infrastructure.Services
{
    /// <summary>
    /// Normalizes LLM output into pretty, predictable HTML:
    /// - Wraps into <article class="nuuz-sparknotes">
    /// - Removes empty sections / placeholder timeline ("N/A")
    /// - Ensures consistent heading hierarchy and spacing
    /// - Adds classes for styling hooks (kicker, cta, section)
    /// </summary>
    public static class SparkNotesFormatter
    {
        public static string Normalize(string rawHtml, string url, string title)
        {
            if (string.IsNullOrWhiteSpace(rawHtml))
                return Fallback(url, title);

            var doc = new HtmlDocument();
            doc.LoadHtml(rawHtml);

            // If the model returned a fragment, create a container
            var root = doc.DocumentNode;
            if (root.SelectSingleNode("//article[contains(@class,'nuuz-sparknotes')]") == null)
            {
                var wrapper = HtmlNode.CreateNode("<article class=\"nuuz-sparknotes\"></article>");
                // move children into wrapper
                foreach (var child in root.ChildNodes.ToList())
                    wrapper.AppendChild(child);
                root.RemoveAllChildren();
                root.AppendChild(wrapper);
            }

            var art = root.SelectSingleNode("//article[contains(@class,'nuuz-sparknotes')]");

            // Ensure a single H2 title "Nuuz SparkNotes"
            var h2 = art.SelectSingleNode(".//h1|.//h2") ?? HtmlNode.CreateNode("<h2>Nuuz SparkNotes</h2>");
            h2.Name = "h2";
            if (!Regex.IsMatch(h2.InnerText ?? "", "nuuz sparknotes", RegexOptions.IgnoreCase))
                h2.InnerHtml = "Nuuz SparkNotes";
            art.PrependChild(h2);

            // Kicker: first <p> should have .kicker
            var firstP = art.SelectSingleNode(".//p[1]");
            if (firstP != null)
            {
                firstP.SetAttributeValue("class", MergeClasses(firstP.GetAttributeValue("class", ""), "kicker"));
                if (string.IsNullOrWhiteSpace(HtmlEntity.DeEntitize(firstP.InnerText)))
                    firstP.Remove();
            }

            // Remove empty bullet lists / paras
            foreach (var ul in art.SelectNodes(".//ul") ?? Enumerable.Empty<HtmlNode>())
                if (!ul.SelectNodes(".//li")?.Any(li => !IsTrivialText(li.InnerText)) ?? true) ul.Remove();

            foreach (var p in art.SelectNodes(".//p") ?? Enumerable.Empty<HtmlNode>())
                if (IsTrivialText(p.InnerText)) p.Remove();

            // Remove placeholder timeline N/A rows
            foreach (var li in art.SelectNodes(".//li") ?? Enumerable.Empty<HtmlNode>())
            {
                var txt = Clean(li.InnerText);
                if (txt.EndsWith("N/A", System.StringComparison.OrdinalIgnoreCase))
                    li.Remove();
            }

            // Remove empty section headings (h3 with no following content block)
            foreach (var h3 in art.SelectNodes(".//h3") ?? Enumerable.Empty<HtmlNode>())
            {
                var next = h3.NextSibling;
                while (next != null && next.NodeType == HtmlNodeType.Text) next = next.NextSibling;
                var hasContent = next != null && (next.Name is "p" or "ul" or "ol" or "blockquote");
                if (!hasContent) h3.Remove();
                else h3.SetAttributeValue("class", MergeClasses(h3.GetAttributeValue("class", ""), "section-title"));
            }

            // Ensure CTA exists and has class
            var cta = art.SelectSingleNode(".//p[a]") ?? HtmlNode.CreateNode("<p class=\"cta\"></p>");
            var a = cta.SelectSingleNode(".//a");
            if (a == null)
            {
                a = HtmlNode.CreateNode($"<a href=\"{url}\" target=\"_blank\" rel=\"noopener nofollow\">Read the original at the source</a>");
                cta.AppendChild(a);
            }
            cta.SetAttributeValue("class", MergeClasses(cta.GetAttributeValue("class", ""), "cta"));

            // Trim excessive whitespace
            var html = art.OuterHtml;
            html = Regex.Replace(html, @"\s{2,}", " ");
            return html.Trim();
        }

        private static bool IsTrivialText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            s = Clean(s);
            return s.Length < 3;
        }

        private static string Clean(string s) =>
            Regex.Replace(HtmlEntity.DeEntitize(s ?? "").Trim(), @"\s+", " ");

        private static string MergeClasses(string existing, string add)
        {
            var set = (existing ?? "").Split(' ', System.StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            set.Add(add);
            return string.Join(" ", set);
        }

        private static string Fallback(string url, string title) =>
            $"<article class=\"nuuz-sparknotes\"><h2>Nuuz SparkNotes</h2><p class=\"kicker\">{System.Net.WebUtility.HtmlEncode(title ?? "Untitled")}</p><p>We couldn’t generate a brief this time.</p><p class=\"cta\"><a href=\"{url}\" target=\"_blank\" rel=\"noopener nofollow\">Read the original</a></p></article>";
    }
}
