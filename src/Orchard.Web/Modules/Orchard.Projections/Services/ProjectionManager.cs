﻿using System;
using System.Collections.Generic;
using System.Linq;
using Orchard.ContentManagement;
using Orchard.Data;
using Orchard.Forms.Services;
using Orchard.Localization;
using Orchard.Projections.Descriptors;
using Orchard.Projections.Descriptors.Filter;
using Orchard.Projections.Descriptors.Layout;
using Orchard.Projections.Descriptors.Property;
using Orchard.Projections.Descriptors.SortCriterion;
using Orchard.Projections.Models;
using Orchard.Tokens;

namespace Orchard.Projections.Services {
    public class ProjectionManager : IProjectionManager {
        private readonly ITokenizer _tokenizer;
        private readonly IEnumerable<IFilterProvider> _filterProviders;
        private readonly IEnumerable<ISortCriterionProvider> _sortCriterionProviders;
        private readonly IEnumerable<ILayoutProvider> _layoutProviders;
        private readonly IEnumerable<IPropertyProvider> _propertyProviders;
        private readonly IContentManager _contentManager;
        private readonly IRepository<QueryPartRecord> _queryRepository;

        public ProjectionManager(
            ITokenizer tokenizer,
            IEnumerable<IFilterProvider> filterProviders,
            IEnumerable<ISortCriterionProvider> sortCriterionProviders,
            IEnumerable<ILayoutProvider> layoutProviders,
            IEnumerable<IPropertyProvider> propertyProviders,
            IContentManager contentManager,
            IRepository<QueryPartRecord> queryRepository) {
            _tokenizer = tokenizer;
            _filterProviders = filterProviders;
            _sortCriterionProviders = sortCriterionProviders;
            _layoutProviders = layoutProviders;
            _propertyProviders = propertyProviders;
            _contentManager = contentManager;
            _queryRepository = queryRepository;
            T = NullLocalizer.Instance;
        }

        public Localizer T { get; set; }

        public IEnumerable<TypeDescriptor<FilterDescriptor>> DescribeFilters() {
            var context = new DescribeFilterContext();

            foreach (var provider in _filterProviders) {
                provider.Describe(context);
            }
            return context.Describe();
        }

        public IEnumerable<TypeDescriptor<SortCriterionDescriptor>> DescribeSortCriteria() {
            var context = new DescribeSortCriterionContext();

            foreach (var provider in _sortCriterionProviders) {
                provider.Describe(context);
            }
            return context.Describe();
        }

        public IEnumerable<TypeDescriptor<LayoutDescriptor>> DescribeLayouts() {
            var context = new DescribeLayoutContext();

            foreach (var provider in _layoutProviders) {
                provider.Describe(context);
            }
            return context.Describe();
        }

        public IEnumerable<TypeDescriptor<PropertyDescriptor>> DescribeProperties() {
            var context = new DescribePropertyContext();

            foreach (var provider in _propertyProviders) {
                provider.Describe(context);
            }
            return context.Describe();
        }

        public FilterDescriptor GetFilter(string category, string type) {
            return DescribeFilters()
                .SelectMany(x => x.Descriptors)
                .FirstOrDefault(x => x.Category == category && x.Type == type);
        }

        public SortCriterionDescriptor GetSortCriterion(string category, string type) {
            return DescribeSortCriteria()
                .SelectMany(x => x.Descriptors)
                .FirstOrDefault(x => x.Category == category && x.Type == type);
        }

        public LayoutDescriptor GetLayout(string category, string type) {
            return DescribeLayouts()
                .SelectMany(x => x.Descriptors)
                .FirstOrDefault(x => x.Category == category && x.Type == type);
        }

        public PropertyDescriptor GetProperty(string category, string type) {
            return DescribeProperties()
                .SelectMany(x => x.Descriptors)
                .FirstOrDefault(x => x.Category == category && x.Type == type);
        }

        public int GetCount(int queryId) {
            return GetCount(queryId, null);
        }

        public int GetCount(int queryId, ContentPart part) {
            var queryRecord = _queryRepository.Get(queryId) ?? throw new ArgumentException("queryId");

            // Prepare tokens.
            Dictionary<string, object> tokens = new Dictionary<string, object>();
            if (part != null) {
                tokens.Add("Content", part.ContentItem);
            }

            var contentQueries = GetContentQueries(queryRecord, Enumerable.Empty<SortCriterionRecord>(), tokens);

            // Aggregate the result of each filter group.
            return queryRecord.FilterGroups.Count > 1 ?
                contentQueries.SelectMany(contentQuery => contentQuery.ListIds()).Distinct().Count() :
                contentQueries.Sum(contentQuery => contentQuery.Count());
        }

        public IEnumerable<ContentItem> GetContentItems(int queryId, int skip = 0, int count = 0) {
            return GetContentItems(queryId, null, skip, count);
        }

        public IEnumerable<ContentItem> GetContentItems(int queryId, ContentPart part, int skip = 0, int count = 0) {
            var availableSortCriteria = DescribeSortCriteria().ToList();

            var queryRecord = _queryRepository.Get(queryId);

            if (queryRecord == null) {
                throw new ArgumentException("queryId");
            }

            var contentItems = new List<ContentItem>();

            // Prepare tokens.
            Dictionary<string, object> tokens = new Dictionary<string, object>();
            if (part != null) {
                tokens.Add("Content", part.ContentItem);
            }

            // Aggregate the result of each filter group.
            foreach (var contentQuery in GetContentQueries(queryRecord, queryRecord.SortCriteria.OrderBy(sc => sc.Position), tokens)) {
                contentItems.AddRange(contentQuery.Slice(skip, count));
            }

            if (queryRecord.FilterGroups.Count <= 1) {
                return contentItems;
            }

            // Re-executing the sorting on the aggregated results.
            var ids = contentItems.Select(c => c.Id).ToArray();

            if (ids.Length == 0) {
                return Enumerable.Empty<ContentItem>();
            }

            var groupQuery = _contentManager.HqlQuery().Where(alias => alias.Named("ci"), x => x.InG("Id", ids));

            // Iterate over each sort criteria to apply the alterations to the query object.
            foreach (var sortCriterion in queryRecord.SortCriteria.OrderBy(s => s.Position)) {
                var tokenizedState = _tokenizer.Replace(sortCriterion.State, tokens);
                var sortCriterionContext = new SortCriterionContext {
                    Query = groupQuery,
                    State = FormParametersHelper.ToDynamic(tokenizedState),
                    QueryPartRecord = queryRecord,
                    Tokens = tokens
                };

                string category = sortCriterion.Category;
                string type = sortCriterion.Type;

                // Find specific sort criterion.
                var descriptor = availableSortCriteria
                    .SelectMany(x => x.Descriptors)
                    .FirstOrDefault(x => x.Category == category && x.Type == type);

                // Skip if not found.
                if (descriptor == null) {
                    continue;
                }

                // Apply alteration.
                descriptor.Sort(sortCriterionContext);

                groupQuery = sortCriterionContext.Query;
            }

            return groupQuery.Slice(0, count);
        }

        public IEnumerable<IHqlQuery> GetContentQueries(
            QueryPartRecord queryRecord,
            IEnumerable<SortCriterionRecord> sortCriteria,
            Dictionary<string, object> tokens) {

            var availableFilters = DescribeFilters().ToList();
            var availableSortCriteria = DescribeSortCriteria().ToList();
            if (tokens == null) {
                tokens = new Dictionary<string, object>();
            }

            var version = queryRecord.VersionScope.ToVersionOptions();

            // Iterate over each filter group and evaluate the filters.
            foreach (var group in queryRecord.FilterGroups) {
                var contentQuery = _contentManager.HqlQuery().ForVersion(version);

                // Iterate over each filter to apply the alterations to the query object.
                foreach (var filter in group.Filters) {
                    var tokenizedState = _tokenizer.Replace(filter.State, tokens);
                    var filterContext = new FilterContext {
                        Query = contentQuery,
                        State = FormParametersHelper.ToDynamic(tokenizedState),
                        QueryPartRecord = queryRecord,
                        Tokens = tokens
                    };

                    string category = filter.Category;
                    string type = filter.Type;

                    // Find specific filter.
                    var descriptor = availableFilters
                        .SelectMany(x => x.Descriptors)
                        .FirstOrDefault(x => x.Category == category && x.Type == type);

                    // Skip if not found.
                    if (descriptor == null) {
                        continue;
                    }

                    // Apply alteration.
                    descriptor.Filter(filterContext);

                    contentQuery = filterContext.Query;
                }

                // Iterate over each sort criteria to apply the alterations to the query object.
                foreach (var sortCriterion in sortCriteria.OrderBy(s => s.Position)) {
                    var tokenizedState = _tokenizer.Replace(sortCriterion.State, tokens);
                    var sortCriterionContext = new SortCriterionContext {
                        Query = contentQuery,
                        State = FormParametersHelper.ToDynamic(tokenizedState),
                        QueryPartRecord = queryRecord,
                        Tokens = tokens
                    };

                    string category = sortCriterion.Category;
                    string type = sortCriterion.Type;

                    // Find specific sort criterion.
                    var descriptor = availableSortCriteria
                        .SelectMany(x => x.Descriptors)
                        .FirstOrDefault(x => x.Category == category && x.Type == type);

                    // Skip if not found.
                    if (descriptor == null) {
                        continue;
                    }

                    // Apply alteration.
                    descriptor.Sort(sortCriterionContext);

                    contentQuery = sortCriterionContext.Query;
                }

                yield return contentQuery;
            }
        }
    }
}
