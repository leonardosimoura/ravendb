using System;
using System.Collections.Generic;
using System.Text;

namespace Raven.Tests.Core.Utils.Entities
{
    public class UserWithGuidId
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string LastName { get; set; }
        public string AddressId { get; set; }
        public int Count { get; set; }
        public int Age { get; set; }
    }
}
