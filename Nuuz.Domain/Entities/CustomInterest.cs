using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Domain.Entities;
[FirestoreData]
public class CustomInterest
{
    [FirestoreProperty("id")]
    public string Id { get; set; } = null!;     // GUID string

    [FirestoreProperty("name")]
    public string Name { get; set; } = null!;
}
