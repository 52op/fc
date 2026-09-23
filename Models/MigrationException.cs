using System;

namespace FC.Models
{
    /// <summary>迁移/还原流程中的业务异常（提示给用户的内容在 Message 里）</summary>
    [Serializable]
    public class MigrationException : Exception
    {
        public MigrationException(string message)
            : base(message)
        {
        }

        public MigrationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
