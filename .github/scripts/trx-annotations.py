# Prints one GitHub Actions error annotation per failed test found in the .trx files under the given folder,
# so the failures show up on the run's summary page (and in the public API) without opening the log.
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def escape(text):
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


failed = 0
for path in glob.glob(os.path.join(sys.argv[1], "**", "*.trx"), recursive=True):
    for result in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", NS):
        if result.get("outcome") != "Failed":
            continue
        failed += 1
        message = result.findtext(".//t:ErrorInfo/t:Message", "", NS).strip()
        stack = result.findtext(".//t:ErrorInfo/t:StackTrace", "", NS).strip()
        # GitHub keeps only the start of a long annotation; the message comes first.
        body = (message + "\n" + "\n".join(stack.splitlines()[:6]))[:3000]
        print(f"::error title={escape(result.get('testName', '?'))}::{escape(body)}")

print(f"{failed} failed test(s)")
