﻿using Dangl.Data.Shared.Json;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Xunit;

namespace Dangl.Data.Shared.Tests.Json
{
    public class CamelCaseDefaultValuesContractResolverTests
    {
        [Fact]
        public void SerializesDateTime()
        {
            var input = new
            {
                DateTimeProp = DateTime.UtcNow
            };
            var serialized = GetSerializedJson(input);
            Assert.Contains("dateTimeProp", serialized);
        }

        [Fact]
        public void SerializesDateTimeOffset()
        {
            var input = new
            {
                DateTimeOffsetProp = DateTimeOffset.UtcNow
            };
            var serialized = GetSerializedJson(input);
            Assert.Contains("dateTimeOffsetProp", serialized);
        }

        [Fact]
        public void SerializesGuid()
        {
            var input = new
            {
                GuidProp = Guid.NewGuid()
            };
            var serialized = GetSerializedJson(input);
            Assert.Contains("guidProp", serialized);
        }

        [Fact]
        public void DoesNotSerializeDefaultDateTime()
        {
            var input = new
            {
                DateTimeProp = default(DateTime)
            };
            var serialized = GetSerializedJson(input);
            Assert.DoesNotContain("DateTimeProp", serialized);
            Assert.DoesNotContain("dateTimeProp", serialized);
        }

        [Fact]
        public void DoesNotSerializeDefaultDateTimeOffset()
        {
            var input = new
            {
                DateTimeOffsetProp = default(DateTimeOffset)
            };
            var serialized = GetSerializedJson(input);
            Assert.DoesNotContain("DateTimeOffsetProp", serialized);
            Assert.DoesNotContain("dateTimeOffsetProp", serialized);
        }

        [Fact]
        public void DoesNotSerializeDefaultGuid()
        {
            var input = new
            {
                GuidProp = default(Guid)
            };
            var serialized = GetSerializedJson(input);
            Assert.DoesNotContain("GuidProp", serialized);
            Assert.DoesNotContain("guidProp", serialized);
        }

        [Fact]
        public void KeepsKeysInOriginalCasingInDictionary()
        {
            var src = new Dictionary<string, string>();
            src["First"] = "ValueFirst";
            src["Second"] = "ValueSecond";
            src["third"] = "ValueThird";

            var serialized = GetSerializedJson(src);

            Assert.Contains("\"First\"", serialized);
            Assert.DoesNotContain("\"first\"", serialized);
            Assert.Contains("\"Second\"", serialized);
            Assert.DoesNotContain("\"second\"", serialized);
            Assert.Contains("\"third\"", serialized);
            Assert.DoesNotContain("\"Third\"", serialized);
        }

        [Fact]
        public void KeepsKeysInOriginalCasingInObservableDictionary()
        {
            var src = new ObservableDictionary<string, string>();
            src["First"] = "ValueFirst";
            src["Second"] = "ValueSecond";
            src["third"] = "ValueThird";

            var serialized = GetSerializedJson(src);

            Assert.Contains("\"First\"", serialized);
            Assert.DoesNotContain("\"first\"", serialized);
            Assert.Contains("\"Second\"", serialized);
            Assert.DoesNotContain("\"second\"", serialized);
            Assert.Contains("\"third\"", serialized);
            Assert.DoesNotContain("\"Third\"", serialized);
        }

        private string GetSerializedJson(object input)
        {
            var jsonOptions = new JsonSerializerSettings();
            jsonOptions.ContractResolver = new CamelCaseDefaultValuesContractResolver();
            var serialized = JsonConvert.SerializeObject(input, jsonOptions);
            return serialized;
        }
    }
}
