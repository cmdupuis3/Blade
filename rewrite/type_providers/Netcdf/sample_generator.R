
library("ncdf4")

path = "C:/Users/cdupu/Documents/netcdf_type_provider/"

xlen = 20
ylen = 30
zlen = 50

xvals = as.integer(1:xlen)
yvals = as.integer(10 + 1:ylen)
zvals = as.integer(5 * 1:zlen)

xdim = list(name="xdim", len=length(xvals), values=xvals)
ydim = list(name="ydim", len=length(yvals), values=yvals)
zdim = list(name="zdim", len=length(zvals), values=zvals)

dims.A = list(xdim, ydim, zdim)

labels.dims.A = lapply(dims.A, FUN=function(x) x$values)
names(labels.dims.A) = lapply(dims.A, FUN=function(x) x$name)

vals.A = array(data = rnorm(prod(as.integer(lapply(dims.A, function(x) x$len)))), 
               dim = as.integer(lapply(dims.A, FUN=function(x) x$len)),
               dimnames = labels.dims.A)

A = list(name="A", dims=dims.A, values=vals.A)
var.list = list(A)


nc.dims.of = function(dim) {
  return (ncdim_def(dim$name, "", dim$values))
}

nc.var.of = function(var) {
  dims = lapply(var$dims, nc.dims.of)
  return (ncvar_def(var$name, "", dims, missval=NaN))
}

nc.vars = lapply(var.list, nc.var.of)

sample.file = nc_create(paste0(path, "sample.nc"), nc.vars, force_v4=TRUE)

for (var in var.list) {
  ncvar_put(sample.file, var$name, var$values)
}

nc_close(sample.file)

