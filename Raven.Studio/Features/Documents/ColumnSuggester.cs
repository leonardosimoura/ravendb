﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Raven.Abstractions.Data;
using Raven.Json.Linq;
using Raven.Studio.Infrastructure;
using Raven.Studio.Models;

namespace Raven.Studio.Features.Documents
{
    public class ColumnSuggester
    {
        private readonly IVirtualCollectionSource<ViewableDocument> source;
        private readonly string context;

        public ColumnSuggester(IVirtualCollectionSource<ViewableDocument> source, string context)
        {
            this.source = source;
            this.context = context;
        }

        private IList<SuggestedColumn> CreateSuggestedColumnsFromDocuments(JsonDocument[] jsonDocuments, bool includeNestedPropeties)
        {
            var columnsByBinding = new Dictionary<string, SuggestedColumn>();
            var foundColumns = jsonDocuments.SelectMany(jDoc => CreateSuggestedColumnsFromJObject(jDoc.DataAsJson, parentPropertyPath: "", includeNestedProperties: includeNestedPropeties));

            foreach (var suggestedColumn in foundColumns)
            {
                if (!columnsByBinding.ContainsKey(suggestedColumn.Binding))
                {
                    columnsByBinding.Add(suggestedColumn.Binding, suggestedColumn);
                }
                else
                {
                    var existingColumn = columnsByBinding[suggestedColumn.Binding];
                    existingColumn.MergeFrom(suggestedColumn);
                }
            }

            return columnsByBinding.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        private IList<SuggestedColumn> CreateSuggestedColumnsFromJObject(RavenJObject jObject, string parentPropertyPath, bool includeNestedProperties)
        {
            return (from property in jObject
                    let path = parentPropertyPath + (string.IsNullOrEmpty(parentPropertyPath) ? "" : ".") + property.Key
                    select new SuggestedColumn()
                    {
                        Header = path,
                        Binding = path,
                        Children = property.Value is RavenJObject && includeNestedProperties 
                                ?  CreateSuggestedColumnsFromJObject(property.Value as RavenJObject, path, includeNestedProperties)
                                : new SuggestedColumn[0]
                    }).ToList();
        }

        public Task<IList<SuggestedColumn>> AutoSuggest()
        {
            return AllSuggestions(includeNestedProperties: false).ContinueWith(t => PickLikelyColumns(t.Result));
        }

        private IList<SuggestedColumn> PickLikelyColumns(IList<SuggestedColumn> allColumns)
        {
            return allColumns.Take(6).ToList();
        }

        public Task<IList<SuggestedColumn>> AllSuggestions()
        {
            return AllSuggestions(includeNestedProperties:true);
        }

        private Task<IList<SuggestedColumn>> AllSuggestions(bool includeNestedProperties)
        {
            return GetSampleDocuments().ContinueWith(t => CreateSuggestedColumnsFromDocuments(t.Result, includeNestedProperties));
        }

        private Task<JsonDocument[]> GetSampleDocuments()
        {
            return source.GetPageAsync(0, 1, null)
                          .ContinueWith(t => new[] {t.Result.Count > 0
                                                 ? t.Result[0].Document
                                                 : default(JsonDocument)});
        }
    }
}
