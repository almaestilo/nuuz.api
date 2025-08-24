using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.DTOs;
public sealed class EmbedCheckResult
{
    public bool Embeddable { get; set; }
    public string? Reason { get; set; }           // e.g., "X-Frame-Options: DENY" or "CSP frame-ancestors: none"
    public string? FinalUrl { get; set; }         // after redirects
    public int StatusCode { get; set; }           // last HTTP status code observed
}
