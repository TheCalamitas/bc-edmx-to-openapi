using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Vocabularies;
using System.Linq;

namespace EdmxToYaml
{
    public static class EdmExtensions
    {
        public static bool GetBooleanCapability(
            this IEdmVocabularyAnnotatable target,
            IEdmModel model,
            string termName,
            string propertyName,
            bool defaultValue)
        {
            var term = model.FindTerm(termName);
            if (term == null) return defaultValue;

            var annotation = model.FindVocabularyAnnotations<IEdmVocabularyAnnotation>(target, term)
                                  .FirstOrDefault();
            if (annotation?.Value is IEdmRecordExpression record)
            {
                var propertyValue = record.FindProperty(propertyName)?.Value;
                if (propertyValue is IEdmBooleanConstantExpression boolConst)
                {
                    return boolConst.Value;
                }
            }
            return defaultValue;
        }
    }
}