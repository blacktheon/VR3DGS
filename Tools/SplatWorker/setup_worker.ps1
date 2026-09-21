$ErrorActionPreference = 'Stop'
$workerRoot = $PSScriptRoot
$projectRoot = [IO.Path]::GetFullPath((Join-Path $workerRoot '../..'))
$setupRoot = Join-Path $projectRoot 'SplatData/setup'
New-Item -ItemType Directory -Force -Path $setupRoot | Out-Null

function Write-SetupStatus([string]$state, [string]$message) {
    $target = Join-Path $setupRoot 'status.json'
    $temporary = Join-Path $setupRoot ('status.' + [guid]::NewGuid().ToString('N') + '.tmp')
    @{schema_version=1;state=$state;message=$message} | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $target) }
}

Start-Transcript -Path (Join-Path $setupRoot 'setup.log') -Append | Out-Null
try {
    Write-SetupStatus 'Running' 'Installing isolated Python 3.11 worker'
    $uvPath = Join-Path $workerRoot '.bootstrap/bin/uv.exe'
    if (-not (Test-Path -LiteralPath $uvPath)) {
        $bootstrap = Join-Path $workerRoot '.bootstrap'
        $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
        if ($launcher) { & $launcher.Source -3 -m pip install 'uv==0.12.17' --target $bootstrap }
        else { & python.exe -m pip install 'uv==0.12.17' --target $bootstrap }
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the project-local uv bootstrap. See setup.log.' }
    }
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $workerRoot '.python'
    $env:UV_CACHE_DIR = Join-Path $workerRoot '.uv-cache'
    $pythonPath = Join-Path $workerRoot '.venv/Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $pythonPath)) {
        & $uvPath venv --python '3.11.16' --seed (Join-Path $workerRoot '.venv')
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Python environment.' }
    }
    & $pythonPath -c 'import sys; assert sys.version_info[:2] == (3,11), "Expected isolated Python 3.11"'
    if ($LASTEXITCODE -ne 0) { throw 'The local worker interpreter has the wrong version.' }
    & $pythonPath -m pip install 'setuptools==75.8.0'
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned build backend.' }
    Write-SetupStatus 'Running' 'Installing PyTorch CUDA runtime and pinned worker dependencies'
    & $pythonPath -m pip install 'torch==2.7.1+cu128' --index-url 'https://download.pytorch.org/whl/cu128'
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned PyTorch CUDA runtime.' }
    $env:BUILD_NO_CUDA = '1'
    & $pythonPath -m pip install --no-build-isolation --extra-index-url 'https://download.pytorch.org/whl/cu128' -r (Join-Path $workerRoot 'requirements.lock')
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned worker dependencies.' }
    Write-SetupStatus 'Succeeded' 'Dependencies installed. Run Check GPU environment to compile and verify rasterization.'
}
catch {
    Write-SetupStatus 'Failed' $_.Exception.Message
    Write-Error $_ -ErrorAction Continue
    exit 1
}
finally { Stop-Transcript | Out-Null }
