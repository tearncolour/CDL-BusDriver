"""Build/check source geometry and regenerate only the CDLC resource."""
from pathlib import Path
path=Path(__file__).with_name('build_model.py')
code=path.read_text(encoding='utf-8')
namespace={'__file__':str(path),'__name__':'__main__'}
exec(compile(code[:code.index('body_objects=')],str(path),'exec'),namespace)
print('RUNTIME_BUILD_OK')
