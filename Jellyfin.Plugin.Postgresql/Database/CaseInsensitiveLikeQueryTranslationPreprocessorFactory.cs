using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Jellyfin.Plugin.Postgresql.Database;

/// <summary>
/// Rewrites <see cref="DbFunctionsExtensions.Like(DbFunctions, string, string)"/> to Npgsql's
/// <c>ILIKE</c> so text matching keeps the case-insensitivity Jellyfin's queries assume.
/// </summary>
/// <remarks>
/// SQLite's <c>LIKE</c> is case-insensitive for ASCII by default; PostgreSQL's is not. Jellyfin
/// relies on the SQLite behaviour in roughly a dozen places — item search against
/// <c>OriginalTitle</c> and every activity-log filter among them — and the difference does not
/// raise an error, it just silently returns fewer rows. Rewriting here rather than patching each
/// call site keeps the plugin free of any dependency on server internals.
/// <para>
/// Cost: <c>ILIKE</c> cannot use a plain B-tree index for prefix patterns. Most of Jellyfin's
/// patterns are <c>%term%</c>, which no B-tree could serve anyway; if profiling shows a prefix
/// search that matters, the fix is a <c>pg_trgm</c> GIN index rather than reverting this.
/// </para>
/// </remarks>
internal sealed class CaseInsensitiveLikeQueryTranslationPreprocessorFactory : IQueryTranslationPreprocessorFactory
{
    private readonly QueryTranslationPreprocessorDependencies _dependencies;
    private readonly RelationalQueryTranslationPreprocessorDependencies _relationalDependencies;

    /// <summary>
    /// Initializes a new instance of the <see cref="CaseInsensitiveLikeQueryTranslationPreprocessorFactory"/> class.
    /// </summary>
    /// <param name="dependencies">Dependencies supplied by EF Core.</param>
    /// <param name="relationalDependencies">Relational dependencies supplied by EF Core.</param>
    public CaseInsensitiveLikeQueryTranslationPreprocessorFactory(
        QueryTranslationPreprocessorDependencies dependencies,
        RelationalQueryTranslationPreprocessorDependencies relationalDependencies)
    {
        _dependencies = dependencies;
        _relationalDependencies = relationalDependencies;
    }

    /// <inheritdoc />
    public QueryTranslationPreprocessor Create(QueryCompilationContext queryCompilationContext)
        => new CaseInsensitiveLikeQueryTranslationPreprocessor(
            _dependencies,
            _relationalDependencies,
            queryCompilationContext);

    private sealed class CaseInsensitiveLikeQueryTranslationPreprocessor : RelationalQueryTranslationPreprocessor
    {
        public CaseInsensitiveLikeQueryTranslationPreprocessor(
            QueryTranslationPreprocessorDependencies dependencies,
            RelationalQueryTranslationPreprocessorDependencies relationalDependencies,
            QueryCompilationContext queryCompilationContext)
            : base(dependencies, relationalDependencies, queryCompilationContext)
        {
        }

        public override Expression Process(Expression query)
            => base.Process(LikeToILikeRewriter.Instance.Visit(query));
    }

    private sealed class LikeToILikeRewriter : ExpressionVisitor
    {
        public static readonly LikeToILikeRewriter Instance = new();

        private static readonly MethodInfo? _like = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)]);

        private static readonly MethodInfo? _likeWithEscape = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)]);

        private static readonly MethodInfo? _iLike = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            [typeof(DbFunctions), typeof(string), typeof(string)]);

        private static readonly MethodInfo? _iLikeWithEscape = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)]);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var replacement = ResolveReplacement(node.Method);
            if (replacement is null)
            {
                return base.VisitMethodCall(node);
            }

            return Expression.Call(replacement, Visit(node.Arguments));
        }

        private static MethodInfo? ResolveReplacement(MethodInfo method)
        {
            if (_like is not null && _iLike is not null && method == _like)
            {
                return _iLike;
            }

            if (_likeWithEscape is not null && _iLikeWithEscape is not null && method == _likeWithEscape)
            {
                return _iLikeWithEscape;
            }

            return null;
        }
    }
}
