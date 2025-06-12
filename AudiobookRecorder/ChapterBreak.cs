using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudiobookRecorder
{
    public struct ChapterBreak
    {
        public TimeSpan Start;

        public string ChapterName;
        public int? ChapterOrdinal;
    }
}
