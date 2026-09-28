#include "geo/shape.h"
#include "geo/circle.h"

namespace geo {

int Shape::count = 0;

Shape::Shape(const char* name) : name_(name)
{
    ++count;
}

Shape::~Shape()
{
    --count;
}

void Shape::accept(Visitor& visitor) const
{
    visitor.visit(*this);
}

void Shape::move(const Point& by)
{
    origin_ += by;
}

void Shape::move(double dx, double dy)
{
    move(Point{dx, dy});
}

Point& Point::operator+=(const Point& other)
{
    x += other.x;
    y += other.y;
    return *this;
}

double distance(const Point& a, const Point& b)
{
    double dx = a.x - b.x;
    double dy = a.y - b.y;
    return dx * dx + dy * dy;
}

} // namespace geo
